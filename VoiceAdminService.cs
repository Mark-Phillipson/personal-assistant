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

    public async Task<string> ListIncompleteTodosAsync(string? projectOrCategory = null, int? maxResults = null, bool asHtmlTable = true)
    {
        return await Task.FromResult("Deprecated: Voice Admin Todos are no longer supported. Use Personal Todos (GitHub Issues) instead.");
    }

    public async Task<string> AddTodoAsync(string title, string? description = null, string? projectOrCategory = null, int sortPriority = 0)
    {
        return await Task.FromResult("Deprecated: Adding Voice Admin Todos is disabled. Use 'add_personal_todo' to create a GitHub Issue in Personal-Todos.");
    }

    public async Task<string> MarkTodoCompleteAsync(int todoId)
    {
        return await Task.FromResult("Deprecated: Completing Voice Admin Todos is disabled. Use 'complete_personal_todo' with the GitHub Issue number instead.");
    }

    public async Task<string> AssignTodoProjectAsync(int todoId, string? projectOrCategory)
    {
        return await Task.FromResult("Deprecated: Assigning projects to Voice Admin Todos is disabled. Use GitHub issue labels to categorize personal todos.");
    }

    public async Task<string> MarkTodoCompleteByTextAsync(string titleOrKeyword, bool exactMatch = false)
    {
        return await Task.FromResult("Deprecated: Completing Voice Admin Todos by text is disabled. Search personal todos with 'list_personal_todos' and close by issue number.");
    }

    public async Task<string> AssignTodoProjectByTextAsync(string titleOrKeyword, string? projectOrCategory, bool exactMatch = false)
    {
        return await Task.FromResult("Deprecated: Assigning Voice Admin todo projects by text is disabled. Use GitHub issue labels to categorize personal todos.");
    }

    public async Task<(bool Success, string Message, IEnumerable<IDictionary<string, object?>> Rows)> GetIncompleteTodosRowsAsync(string? projectOrCategory = null, int? maxResults = null)
    {
        return (false, "Deprecated: Voice Admin Todos are no longer supported. Use Personal Todos (GitHub Issues) instead.", Array.Empty<IDictionary<string, object?>>());
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

    private SqliteConnection CreateReadOnlyConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    private SqliteConnection CreateReadWriteConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    private static async Task<List<TodoMatch>> FindActiveTodoMatchesAsync(SqliteConnection conn, string titleOrKeyword, bool exactMatch, int limit)
    {
        var sql = exactMatch
            ? """
                SELECT Id, Title, Project, SortPriority, Created
                FROM Todos
                WHERE COALESCE(Completed, 0) = 0
                  AND COALESCE(Archived, 0) = 0
                  AND lower(trim(COALESCE(Title, ''))) = lower(trim(@kw))
                ORDER BY COALESCE(SortPriority, 0) DESC,
                         COALESCE(Created, '') DESC,
                         Id DESC
                LIMIT @limit
                """
            : """
                SELECT Id, Title, Project, SortPriority, Created
                FROM Todos
                WHERE COALESCE(Completed, 0) = 0
                  AND COALESCE(Archived, 0) = 0
                  AND (
                        COALESCE(Title, '') LIKE @kwLike
                        OR COALESCE(Description, '') LIKE @kwLike
                        OR COALESCE(Project, '') LIKE @kwLike
                      )
                ORDER BY COALESCE(SortPriority, 0) DESC,
                         COALESCE(Created, '') DESC,
                         Id DESC
                LIMIT @limit
                """;

        await using var cmd = new SqliteCommand(sql, conn);
        if (exactMatch)
        {
            cmd.Parameters.AddWithValue("@kw", titleOrKeyword);
        }
        else
        {
            cmd.Parameters.AddWithValue("@kwLike", $"%{titleOrKeyword}%");
        }

        cmd.Parameters.AddWithValue("@limit", limit);

        var matches = new List<TodoMatch>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            matches.Add(new TodoMatch(
                Id: reader.GetInt32(0),
                Title: reader.IsDBNull(1) ? "(untitled)" : reader.GetString(1),
                Project: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                SortPriority: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Created: reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }

        return matches;
    }

    private static async Task<bool> TryMarkTodoCompleteAsync(SqliteConnection conn, int todoId)
    {
        const string sql = """
            UPDATE Todos
            SET Completed = 1
            WHERE Id = @id
              AND COALESCE(Archived, 0) = 0
              AND COALESCE(Completed, 0) = 0
            """;

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", todoId);
        var updated = await cmd.ExecuteNonQueryAsync();
        return updated > 0;
    }

    private static string BuildAmbiguousTodoMatchMessage(string titleOrKeyword, IReadOnlyList<TodoMatch> matches, string action)
    {
        var builder = new StringBuilder();
        builder.Append($"I found multiple open todos matching '{titleOrKeyword}'. Please provide a TodoId to {action}:\n");

        foreach (var match in matches)
        {
            var project = string.IsNullOrWhiteSpace(match.Project) ? "(none)" : match.Project;
            var created = string.IsNullOrWhiteSpace(match.Created) ? "(unknown)" : match.Created;
            builder.Append("[TodoId:")
                .Append(match.Id)
                .Append("] ")
                .Append(match.Title)
                .Append(" (Project/Category: ")
                .Append(project)
                .Append(", Priority: ")
                .Append(match.SortPriority)
                .Append(", Created: ")
                .Append(created)
                .AppendLine(")");
        }

        return builder.ToString().TrimEnd();
    }

    private sealed record TodoMatch(int Id, string Title, string Project, int SortPriority, string Created);

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

    private static string BuildHtmlTodoTable(string? projectOrCategory, IReadOnlyList<(int id, string title, string project, string created, int sortPriority, string description)> rows)
    {
        const string idHeader = "TodoId";
        const string titleHeader = "Title";
        const string projectHeader = "Project";
        const string priorityHeader = "Priority";
        const string createdHeader = "Created";

        var idWidth = Math.Max(idHeader.Length, rows.Select(row => row.id.ToString().Length).DefaultIfEmpty(idHeader.Length).Max());
        // Keep the line length compact so Telegram doesn't soft-wrap table rows.
        var titleWidth = 22;
        var projectWidth = 14;
        var priorityWidth = Math.Max(priorityHeader.Length, rows.Select(row => row.sortPriority.ToString().Length).DefaultIfEmpty(priorityHeader.Length).Max());
        var createdWidth = 16;

        var filterSuffix = string.IsNullOrWhiteSpace(projectOrCategory)
            ? string.Empty
            : $" for '{projectOrCategory}'";

        var builder = new StringBuilder();
        builder.Append("<b>")
            .Append(EscapeHtml($"Found {rows.Count} incomplete Voice Admin todo item(s){filterSuffix}"))
            .AppendLine("</b>")
            .AppendLine("<pre>")
            .AppendLine($"{idHeader.PadRight(idWidth)} | {titleHeader.PadRight(titleWidth)} | {projectHeader.PadRight(projectWidth)} | {priorityHeader.PadRight(priorityWidth)} | {createdHeader}")
            .AppendLine($"{new string('-', idWidth)}-+-{new string('-', titleWidth)}-+-{new string('-', projectWidth)}-+-{new string('-', priorityWidth)}-+-{new string('-', createdWidth)}");

        foreach (var row in rows)
        {
            var titleCell = SanitizeTableCell(row.title).Replace(" | ", " • ");
            var projectCell = SanitizeTableCell(row.project).Replace(" | ", " • ");
            builder.Append(row.id.ToString().PadRight(idWidth))
                .Append(" | ")
                .Append(EscapeHtml(TrimToWidth(titleCell, titleWidth)).PadRight(titleWidth))
                .Append(" | ")
                .Append(EscapeHtml(TrimToWidth(projectCell, projectWidth)).PadRight(projectWidth))
                .Append(" | ")
                .Append(row.sortPriority.ToString().PadRight(priorityWidth))
                .Append(" | ")
                .AppendLine(EscapeHtml(TrimToWidth(SanitizeTableCell(row.created), createdWidth)).PadRight(createdWidth));
        }

        builder.AppendLine("</pre>")
            .AppendLine("<b>Tip:</b> To view more details for a specific item, use complete_voice_admin_todo_by_text or the TodoId from the table.");

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