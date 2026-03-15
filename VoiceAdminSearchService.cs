using System.Text;
using Microsoft.Data.Sqlite;

internal sealed class VoiceAdminSearchService
{
    private static readonly string[] PreferredDisplayColumns =
    [
        "Name",
        "Title",
        "Command",
        "CommandLine",
        "Category",
        "CategoryName",
        "Key",
        "Value",
        "Description",
        "Action",
        "Arguments",
        "Notes"
    ];

    private readonly string? _dbPath;
    private readonly int _maxResults;

    private VoiceAdminSearchService(string? dbPath, int maxResults)
    {
        _dbPath = dbPath;
        _maxResults = maxResults;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_dbPath) && File.Exists(_dbPath);

    public static VoiceAdminSearchService FromEnvironment()
    {
        var dbPath = EnvironmentSettings.ReadOptionalString("VOICE_ADMIN_DB_PATH")
            ?? EnvironmentSettings.ReadOptionalString("VOICE_LAUNCHER_DB_PATH");
        var maxResults = EnvironmentSettings.ReadInt("VOICE_ADMIN_MAX_RESULTS", fallback: 20, min: 1, max: 100);
        return new VoiceAdminSearchService(dbPath, maxResults);
    }

    public string GetSetupStatusText()
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
            return "VOICE_ADMIN_DB_PATH is not set (fallback: VOICE_LAUNCHER_DB_PATH). Set one to the full path of your Voice Admin SQLite database.";

        if (!File.Exists(_dbPath))
            return $"Voice Admin database not found at: {_dbPath}";

        return $"Voice Admin database is configured and accessible at: {_dbPath}";
    }

    public Task<string> SearchTalonCommandsAsync(string keyword, int? maxResults = null)
        => SearchTableAsync("Talon Commands", keyword, maxResults);

    public Task<string> SearchCustomInTeleSenseAsync(string keyword, int? maxResults = null)
        => SearchTableAsync("Custom in Tele Sense", keyword, maxResults);

    public Task<string> SearchValuesAsync(string keyword, int? maxResults = null)
        => SearchTableAsync("Values", keyword, maxResults);

    public Task<string> SearchTransactionsAsync(string keyword, int? maxResults = null)
        => SearchTableAsync("Transactions", keyword, maxResults);

    public async Task<string> SearchTableAsync(string requestedTable, string keyword, int? maxResults = null)
    {
        if (!IsConfigured)
            return GetSetupStatusText();

        if (string.IsNullOrWhiteSpace(keyword))
            return "Please provide a search keyword.";

        var limit = Math.Clamp(maxResults ?? _maxResults, 1, _maxResults);

        try
        {
            await using var conn = CreateReadOnlyConnection();
            await conn.OpenAsync();

            var resolvedTable = await ResolveTableNameAsync(conn, requestedTable);
            if (resolvedTable is null)
                return $"Table '{requestedTable}' was not found in the configured Voice Admin database.";

            var columns = await GetTableColumnsAsync(conn, resolvedTable);
            if (columns.Count == 0)
                return $"Table '{resolvedTable}' has no readable columns.";

            var whereClause = string.Join(" OR ", columns.Select(column => $"COALESCE(CAST({QuoteIdentifier(column)} AS TEXT), '') LIKE @kw"));

            var sql = $"""
                SELECT rowid, *
                FROM {QuoteIdentifier(resolvedTable)}
                WHERE {whereClause}
                ORDER BY rowid
                LIMIT @limit
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new StringBuilder();
            var count = 0;

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                count++;
                var rowId = reader.GetInt64(0);
                var rowPreview = BuildRowPreview(reader, columns);
                results.AppendLine($"[RowId:{rowId}] {rowPreview}");
            }

            if (count == 0)
                return $"No rows found in '{resolvedTable}' matching '{keyword}'.";

            return $"Found {count} row(s) in '{resolvedTable}' matching '{keyword}':\n{results}";
        }
        catch (Exception ex)
        {
            return $"Error searching table '{requestedTable}': {ex.Message}";
        }
    }

    public async Task<VoiceAdminCellReadResult> ReadCellValueAsync(string requestedTable, long rowId, string columnName)
    {
        if (!IsConfigured)
            return VoiceAdminCellReadResult.Failure(GetSetupStatusText());

        if (rowId <= 0)
            return VoiceAdminCellReadResult.Failure("Row ID must be a positive integer.");

        if (string.IsNullOrWhiteSpace(columnName))
            return VoiceAdminCellReadResult.Failure("Please provide a column name to copy from.");

        try
        {
            await using var conn = CreateReadOnlyConnection();
            await conn.OpenAsync();

            var resolvedTable = await ResolveTableNameAsync(conn, requestedTable);
            if (resolvedTable is null)
                return VoiceAdminCellReadResult.Failure($"Table '{requestedTable}' was not found in the configured Voice Admin database.");

            var columns = await GetTableColumnsAsync(conn, resolvedTable);
            var resolvedColumn = columns.FirstOrDefault(column =>
                string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase));

            if (resolvedColumn is null)
                return VoiceAdminCellReadResult.Failure(
                    $"Column '{columnName}' was not found in table '{resolvedTable}'. Available columns: {string.Join(", ", columns)}");

            var sql = $"""
                SELECT CAST({QuoteIdentifier(resolvedColumn)} AS TEXT)
                FROM {QuoteIdentifier(resolvedTable)}
                WHERE rowid = @rowId
                LIMIT 1
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@rowId", rowId);

            var raw = await cmd.ExecuteScalarAsync();
            if (raw is null || raw == DBNull.Value)
            {
                return VoiceAdminCellReadResult.Failure(
                    $"The value in '{resolvedTable}.{resolvedColumn}' for RowId {rowId} is empty.");
            }

            var text = Convert.ToString(raw)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return VoiceAdminCellReadResult.Failure(
                    $"The value in '{resolvedTable}.{resolvedColumn}' for RowId {rowId} is empty.");
            }

            return VoiceAdminCellReadResult.FromSuccess(text, resolvedTable, resolvedColumn, rowId);
        }
        catch (Exception ex)
        {
            return VoiceAdminCellReadResult.Failure($"Error reading value from table '{requestedTable}': {ex.Message}");
        }
    }

    private SqliteConnection CreateReadOnlyConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    private static async Task<string?> ResolveTableNameAsync(SqliteConnection conn, string requestedTable)
    {
        var tables = await GetUserTableNamesAsync(conn);
        if (tables.Count == 0)
            return null;

        var requestedNorm = Normalise(requestedTable);
        if (string.IsNullOrWhiteSpace(requestedNorm))
            return null;

        var exact = tables.FirstOrDefault(table => string.Equals(Normalise(table), requestedNorm, StringComparison.Ordinal));
        if (exact is not null)
            return exact;

        var fuzzy = tables.FirstOrDefault(table =>
            Normalise(table).Contains(requestedNorm, StringComparison.Ordinal)
            || requestedNorm.Contains(Normalise(table), StringComparison.Ordinal));

        return fuzzy;
    }

    private static async Task<List<string>> GetUserTableNamesAsync(SqliteConnection conn)
    {
        const string sql = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var cmd = new SqliteCommand(sql, conn);
        var tableNames = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0))
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        return tableNames;
    }

    private static async Task<List<string>> GetTableColumnsAsync(SqliteConnection conn, string tableName)
    {
        var sql = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        await using var cmd = new SqliteCommand(sql, conn);
        var columns = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static string BuildRowPreview(SqliteDataReader reader, IReadOnlyList<string> columns)
    {
        var preferredValues = PreferredDisplayColumns
            .Select(preferred => columns.FirstOrDefault(column => string.Equals(column, preferred, StringComparison.OrdinalIgnoreCase)))
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Select(column => BuildColumnPreview(column!, ReadColumnAsString(reader, columns, column!)))
            .Where(preview => !string.IsNullOrWhiteSpace(preview))
            .Take(4)
            .ToList();

        if (preferredValues.Count > 0)
            return string.Join(" | ", preferredValues);

        var fallbackValues = new List<string>();
        for (var index = 0; index < columns.Count && fallbackValues.Count < 4; index++)
        {
            var value = ReadColumnAsString(reader, columns, columns[index]);
            var preview = BuildColumnPreview(columns[index], value);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                fallbackValues.Add(preview);
            }
        }

        return fallbackValues.Count > 0
            ? string.Join(" | ", fallbackValues)
            : "(all fields empty)";
    }

    private static string ReadColumnAsString(SqliteDataReader reader, IReadOnlyList<string> columns, string columnName)
    {
        var index = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], columnName, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return string.Empty;

        var readerIndex = index + 1;
        if (reader.IsDBNull(readerIndex))
            return string.Empty;

        return Convert.ToString(reader.GetValue(readerIndex)) ?? string.Empty;
    }

    private static string BuildColumnPreview(string columnName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (cleaned.Length > 120)
        {
            cleaned = cleaned[..117] + "...";
        }

        return $"{columnName}: {cleaned}";
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string Normalise(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }
}

internal sealed record VoiceAdminCellReadResult(
    bool Success,
    string Message,
    string? Value = null,
    string? Table = null,
    string? Column = null,
    long? RowId = null)
{
    public static VoiceAdminCellReadResult Failure(string message) => new(false, message);

    public static VoiceAdminCellReadResult FromSuccess(string value, string table, string column, long rowId)
        => new(true, "OK", value, table, column, rowId);
}