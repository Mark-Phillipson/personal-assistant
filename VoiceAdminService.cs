using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;

internal sealed class VoiceAdminService
{
    private readonly string? _dbPath;
    private readonly int _maxResults;

    private VoiceAdminService(string? dbPath, int maxResults)
    {
        _dbPath = dbPath;
        _maxResults = maxResults;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_dbPath) && File.Exists(_dbPath);

    public static VoiceAdminService FromEnvironment()
    {
        var dbPath = EnvironmentSettings.ReadOptionalString("VOICE_ADMIN_DB_PATH")
            ?? EnvironmentSettings.ReadOptionalString("VOICE_LAUNCHER_DB_PATH");
        var maxResults = EnvironmentSettings.ReadInt(
            "VOICE_ADMIN_MAX_RESULTS",
            fallback: EnvironmentSettings.ReadInt("VOICE_LAUNCHER_MAX_RESULTS", fallback: 20, min: 1, max: 100),
            min: 1,
            max: 100);

        return new VoiceAdminService(dbPath, maxResults);
    }

    public string GetSetupStatusText()
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
            return "VOICE_ADMIN_DB_PATH is not set (legacy fallback: VOICE_LAUNCHER_DB_PATH). Set one to the full path of the Voice Admin SQLite database.";

        if (!File.Exists(_dbPath))
            return $"Voice Admin database not found at: {_dbPath}";

        return $"Voice Admin database is configured and accessible at: {_dbPath}";
    }

    /// <summary>Search launcher entries by keyword across Name, CommandLine, and CategoryName.</summary>
    public async Task<string> SearchLauncherEntriesAsync(string keyword, int? maxResults = null, bool asHtmlTable = false)
    {
        if (!IsConfigured)
            return GetSetupStatusText();

        if (string.IsNullOrWhiteSpace(keyword))
            return "Please provide a search keyword.";

        var limit = Math.Min(maxResults ?? _maxResults, _maxResults);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        try
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            const string sql = """
                SELECT DISTINCT l.ID, l.Name, l.CommandLine, l.Arguments, c.Category
                FROM Launcher l
                LEFT JOIN Categories c ON c.ID = l.CategoryID
                WHERE l.Name LIKE @kw
                   OR l.CommandLine LIKE @kw
                   OR c.Category LIKE @kw
                ORDER BY c.Category, l.Name
                LIMIT @limit
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new StringBuilder();
            var count = 0;
            var tableRows = new List<(int id, string name, string category, string command)>();

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                count++;
                var id = reader.GetInt32(0);
                var name = reader.IsDBNull(1) ? "(no name)" : reader.GetString(1);
                var commandLine = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var arguments = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var categoryName = reader.IsDBNull(4) ? "Uncategorised" : reader.GetString(4);

                var argDisplay = string.IsNullOrWhiteSpace(arguments) ? "" : $" {arguments}";
                tableRows.Add((id, name, categoryName, $"{commandLine}{argDisplay}"));
                results.AppendLine($"[ID:{id}] {name} (Category: {categoryName}) -> {commandLine}{argDisplay}");
            }

            if (count == 0)
                return $"No Voice Admin launcher records found matching '{keyword}'.";

            if (asHtmlTable)
                return BuildHtmlLauncherTable(keyword, tableRows);

            return $"Found {count} Voice Admin launcher record(s) matching '{keyword}':\n{results}";
        }
        catch (Exception ex)
        {
            return $"Error searching Voice Admin launcher records: {ex.Message}";
        }
    }

    /// <summary>
    /// Launch a Voice Admin launcher entry by its numeric ID (obtained from a prior search).
    /// The command line is read from the database - the caller never supplies an executable path.
    /// </summary>
    public async Task<string> LaunchLauncherByIdAsync(int launcherId)
    {
        if (!IsConfigured)
            return GetSetupStatusText();

        if (launcherId <= 0)
            return "Launcher ID must be a positive integer.";

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        try
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            const string sql = "SELECT ID, Name, CommandLine, Arguments FROM Launcher WHERE ID = @id LIMIT 1";
            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", launcherId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return $"No launcher found with ID {launcherId}. Use search_voice_admin_launchers to find valid IDs.";

            var name = reader.IsDBNull(1) ? $"ID {launcherId}" : reader.GetString(1);
            var commandLine = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
            var arguments = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim();

            if (string.IsNullOrWhiteSpace(commandLine))
                return $"Launcher '{name}' (ID: {launcherId}) has no command line configured and cannot be launched.";

            var psi = new ProcessStartInfo
            {
                FileName = commandLine,
                Arguments = arguments,
                UseShellExecute = true
            };

            Process.Start(psi);
            return $"Launched '{name}' (ID: {launcherId}).";
        }
        catch (Exception ex)
        {
            return $"Error launching entry {launcherId}: {ex.Message}";
        }
    }

    private static string BuildHtmlLauncherTable(string keyword, IReadOnlyList<(int id, string name, string category, string command)> rows)
    {
        const string idHeader = "ID";
        const string nameHeader = "Name";
        const string categoryHeader = "Category";
        const string commandHeader = "Command";

        var idWidth = Math.Max(idHeader.Length, rows.Select(row => row.id.ToString().Length).DefaultIfEmpty(idHeader.Length).Max());
        var nameWidth = 28;
        var categoryWidth = 20;
        var commandWidth = 52;

        var builder = new StringBuilder();
        builder.Append("<b>")
            .Append(EscapeHtml($"Found {rows.Count} Voice Admin launcher record(s) matching '{keyword}'"))
            .AppendLine("</b>")
            .AppendLine("<pre>")
            .AppendLine($"{idHeader.PadRight(idWidth)} | {nameHeader.PadRight(nameWidth)} | {categoryHeader.PadRight(categoryWidth)} | {commandHeader}")
            .AppendLine($"{new string('-', idWidth)}-+-{new string('-', nameWidth)}-+-{new string('-', categoryWidth)}-+-{new string('-', commandWidth)}");

        foreach (var row in rows)
        {
            builder.Append(row.id.ToString().PadRight(idWidth))
                .Append(" | ")
                .Append(EscapeHtml(TrimToWidth(SanitizeTableCell(row.name), nameWidth)).PadRight(nameWidth))
                .Append(" | ")
                .Append(EscapeHtml(TrimToWidth(SanitizeTableCell(row.category), categoryWidth)).PadRight(categoryWidth))
                .Append(" | ")
                .AppendLine(EscapeHtml(TrimToWidth(SanitizeTableCell(row.command), commandWidth)));
        }

        builder.Append("</pre>");
        return builder.ToString();
    }

    private static string SanitizeTableCell(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value
            .Replace("\r", string.Empty)
            .Replace("\n", " ")
            .Trim();
    }

    private static string TrimToWidth(string value, int width)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= width)
            return value;

        return value[..(width - 3)] + "...";
    }

    private static string EscapeHtml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }
}