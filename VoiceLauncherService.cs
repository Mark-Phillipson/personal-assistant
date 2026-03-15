using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;

internal sealed class VoiceLauncherService
{
    private readonly string? _dbPath;
    private readonly int _maxResults;

    private VoiceLauncherService(string? dbPath, int maxResults)
    {
        _dbPath = dbPath;
        _maxResults = maxResults;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_dbPath) && File.Exists(_dbPath);

    public static VoiceLauncherService FromEnvironment()
    {
        var dbPath = EnvironmentSettings.ReadOptionalString("VOICE_LAUNCHER_DB_PATH");
        var maxResults = EnvironmentSettings.ReadInt("VOICE_LAUNCHER_MAX_RESULTS", fallback: 20, min: 1, max: 100);
        return new VoiceLauncherService(dbPath, maxResults);
    }

    public string GetSetupStatusText()
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
            return "VOICE_LAUNCHER_DB_PATH is not set. Set it to the full path of the VoiceLauncher SQLite database.";

        if (!File.Exists(_dbPath))
            return $"VoiceLauncher database not found at: {_dbPath}";

        return $"VoiceLauncher database is configured and accessible at: {_dbPath}";
    }

    /// <summary>Search launchers by keyword across Name, CommandLine, and CategoryName.</summary>
    public async Task<string> SearchLaunchersAsync(string keyword, int? maxResults = null)
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
            int count = 0;

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
                results.AppendLine($"[ID:{id}] {name} (Category: {categoryName}) \u2192 {commandLine}{argDisplay}");
            }

            if (count == 0)
                return $"No launcher records found matching '{keyword}'.";

            return $"Found {count} launcher(s) matching '{keyword}':\n{results}";
        }
        catch (Exception ex)
        {
            return $"Error searching launchers: {ex.Message}";
        }
    }

    /// <summary>
    /// Launch a launcher entry by its numeric ID (obtained from a prior search).
    /// The command line is read from the database – the caller never supplies an executable path.
    /// </summary>
    public async Task<string> LaunchByIdAsync(int launcherId)
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
                return $"No launcher found with ID {launcherId}. Use search_voice_launchers to find valid IDs.";

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
}
