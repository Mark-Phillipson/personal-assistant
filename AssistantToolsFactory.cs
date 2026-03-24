using System.ComponentModel;
using Microsoft.Extensions.AI;

internal static class AssistantToolsFactory
{
    public static List<AIFunction> Build(
        GmailAssistantService gmailService,
        GoogleCalendarAssistantService calendarService,
        NaturalCommandsAssistantService naturalCommandsService,
        ClipboardAssistantService clipboardService,
        DadJokeService dadJokeService,
        WebBrowserAssistantService webBrowserService,
        VoiceAdminService voiceAdminService,
        VoiceAdminSearchService voiceAdminSearchService,
        GenericDatabaseService genericDatabaseService,
        TalonUserDirectoryService talonUserDirectoryService,
        KnownFolderExplorerService knownFolderExplorerService,
        PodcastSubscriptionsService podcastSubscriptionsService,
        ClipboardHistoryService clipboardHistoryService)
    {
        return
        [
            AIFunctionFactory.Create(
                ([Description("Optional email address that should own Gmail access")] string? expectedAccount = null) =>
                    gmailService.GetSetupStatus(expectedAccount),
                "gmail_setup_status",
                "Check whether Gmail integration is configured and return setup instructions when it is not."),
            AIFunctionFactory.Create(
                async (
                    [Description("Maximum number of inbox emails to return (1-20)")] int maxResults = 5,
                    [Description("Optional Gmail query filter (for example: from:amazon newer_than:7d)")] string? query = null,
                    [Description("When true (default), only return unread emails. Set to false to search all emails regardless of read status.")] bool unreadOnly = true) =>
                    await gmailService.ListUnreadMessagesAsync(maxResults, query, unreadOnly),
                "list_unread_gmail",
                "List inbox messages from Gmail with subject, sender, date, and snippet. By default returns only unread messages; set unreadOnly=false to search all messages including already-read ones."),
            AIFunctionFactory.Create(
                async ([Description("Gmail message id returned by list_unread_gmail")] string messageId) =>
                    await gmailService.ReadMessageAsync(messageId),
                "read_gmail_message",
                "Read details and body preview of a Gmail message by its message id."),
            AIFunctionFactory.Create(
                ([Description("Optional email address that should own Calendar access")] string? expectedAccount = null) =>
                    calendarService.GetSetupStatusText(),
                "calendar_setup_status",
                "Check whether Google Calendar integration is configured and return setup instructions when it is not."),
            AIFunctionFactory.Create(
                async ([Description("Maximum number of upcoming events to return (1-20)")] int maxResults = 5) =>
                    await calendarService.ListUpcomingEventsAsync(maxResults),
                "list_upcoming_calendar_events",
                "List upcoming events from Google Calendar with summary, start, and end times."),
            AIFunctionFactory.Create(
                async (
                    [Description("Event summary/title")] string summary,
                    [Description("Event description")] string description,
                    [Description("Event start time (ISO 8601, UTC)")] DateTime start,
                    [Description("Event end time (ISO 8601, UTC)")] DateTime end) =>
                    await calendarService.CreateEventAsync(summary, description, start, end),
                "create_calendar_event",
                "Create a new event in Google Calendar with summary, description, start, and end times."),
            AIFunctionFactory.Create(
                async ([Description("NaturalCommands text, for example: show desktop")] string commandText) =>
                    await naturalCommandsService.ExecuteForAssistantAsync(commandText),
                "run_natural_command",
                "Run a local NaturalCommands command on the machine hosting this bot."),
            AIFunctionFactory.Create(
                () => clipboardService.GetSetupStatusText(),
                "clipboard_setup_status",
                "Check whether clipboard integration is available on the machine hosting this bot."),
            AIFunctionFactory.Create(
                async ([Description("Optional search term to find a dad joke (for example: 'chicken')")] string? term = null) =>
                    await dadJokeService.GetJokeAsync(term),
                "get_dad_joke",
                "Get a random dad joke or search for a joke containing a keyword."),
            AIFunctionFactory.Create(
                async ([Description("Exact text to copy to the system clipboard")] string text) =>
                    await clipboardService.SetClipboardTextForAssistantAsync(text),
                "set_clipboard_text",
                "Copy text to the system clipboard of the machine hosting this bot. Use this when the user asks to copy, place, or put text on the clipboard."),
            AIFunctionFactory.Create(
                () => webBrowserService.GetSetupStatusText(),
                "web_browser_status",
                "Check whether the web browser (Playwright) integration is available."),
            AIFunctionFactory.Create(
                async ([Description("Full URL to open in the default browser on the host machine")] string url) =>
                    await webBrowserService.OpenInDefaultBrowserAsync(url),
                "open_in_default_browser",
                "Open a URL in the host machine default browser. Use this when the user asks to open a site or app page visually."),
            AIFunctionFactory.Create(
                async ([Description("Full URL to navigate to (e.g. https://upwork.com/search/jobs/?q=Blazor)")] string url) =>
                    await webBrowserService.NavigateAndReadAsync(url),
                "navigate_and_read_page",
                "Navigate to a URL using a real browser and return the readable text content of the page. Use this to visit any website and read its content."),
            AIFunctionFactory.Create(
                async ([Description("YouTube search query (for example: Ukraine the latest)")] string query) =>
                    await webBrowserService.PlayTopYouTubeResultAsync(query, podcastMode: false),
                "play_youtube_top_result",
                "Find the top YouTube result for a query and play it in the host machine default browser with autoplay."),
            AIFunctionFactory.Create(
                async ([Description("Podcast topic or show name (for example: Ukraine war daily briefing)")] string query) =>
                    await webBrowserService.PlayTopYouTubeResultAsync(query, podcastMode: true),
                "play_latest_youtube_podcast",
                "Find and play the top current podcast-style result for a topic. Uses a YouTube Music URL first on the host machine and falls back to YouTube when needed."),
            AIFunctionFactory.Create(
                async ([Description("Spotify search query (for example: Master of Puppets or Metallica latest album)")] string query) =>
                    await webBrowserService.OpenSpotifySearchAsync(query),
                "open_spotify_search",
                "Open Spotify search in the host machine browser for a query. Use this when the user wants Spotify content but exact API-controlled playback is not available."),
            AIFunctionFactory.Create(
                async ([Description("Artist name to find the latest Spotify album for (for example: Metallica)")] string artistName) =>
                    await webBrowserService.PlayLatestSpotifyAlbumAsync(artistName),
                "play_latest_spotify_album",
                "Find the most likely latest Spotify album for an artist and open it in the host machine browser. This uses browser search/open rather than Spotify API playback control."),
            AIFunctionFactory.Create(
                async ([Description("Optional activity or mood such as coding, studying, contemplation, or deep work. Default is general focus music.")] string? activity = null) =>
                    await webBrowserService.PlaySpotifyFocusMusicAsync(activity),
                "play_spotify_focus_music",
                "Open Spotify results for instrumental focus music without lyrics. Use this for requests like play music to code by, study music, deep work music, or contemplation music."),
            AIFunctionFactory.Create(
                async ([Description("Optional activity or mood such as coding, studying, contemplation, or deep work. Default is general focus music.")] string? activity = null) =>
                    await webBrowserService.PlayYouTubeMusicFocusAsync(activity),
                "play_youtube_music_focus",
                "Open YouTube Music results for instrumental focus music without lyrics. Use this as an alternative to Spotify for focus/concentration music."),
            AIFunctionFactory.Create(
                async (
                    [Description("Optional activity or mood such as coding, studying, contemplation, or deep work. Default is general focus music.")] string? activity = null,
                    [Description("Optional preferred service: 'spotify' (default) or 'youtube music'. Defaults to Spotify with YouTube Music as fallback.")] string? preferredService = null) =>
                    await webBrowserService.PlayFocusMusicAsync(activity, preferredService),
                "play_focus_music",
                "Open focus music on your preferred service (Spotify by default, falling back to YouTube Music). Use this for requests like play music to code by, study music, or contemplation music."),
            AIFunctionFactory.Create(
                async ([Description("Search query (e.g. Blazor developer jobs Upwork)")] string query) =>
                    await webBrowserService.SearchWebAsync(query),
                "search_web",
                "Search the web using Bing and return the results. Use this to look up information, find jobs, news, or anything else on the internet."),
            AIFunctionFactory.Create(
                async (
                    [Description("Full URL of the page containing the form")] string url,
                    [Description("JSON object mapping form field name/id to value, e.g. {\"firstName\":\"Carla\",\"lastName\":\"Schmid\"}")] string formFieldsJson,
                    [Description("When true, attempt to submit the form after filling. Default is false.")] bool submitForm = false) =>
                    await webBrowserService.FillWebFormAsync(url, formFieldsJson, submitForm),
                "fill_web_form",
                "Navigate to a URL and fill in web form fields by their name or id attributes. Provide the fields as a JSON object. Optionally submit the form."),
            AIFunctionFactory.Create(
                async () =>
                    await webBrowserService.GetUpworkSessionStatusAsync(),
                "upwork_session_status",
                "Check the current status of the Upwork browser-assisted session and whether a room page is open."),
            AIFunctionFactory.Create(
                async () =>
                    await webBrowserService.OpenUpworkMessagesPortalAsync(),
                "upwork_open_messages_portal",
                "Open Upwork messages in the automation browser for logged-in room workflows. Use this before reading room context or drafting replies."),
            AIFunctionFactory.Create(
                async ([Description("How many latest messages to capture from the current room (1-30)")] int latestMessageCount = 8) =>
                    await webBrowserService.ReadUpworkCurrentRoomContextAsync(latestMessageCount),
                "upwork_read_current_room",
                "Read the currently open Upwork message room context, including room id, counterpart, and latest messages for reply drafting."),
            AIFunctionFactory.Create(
                async (
                    [Description("Reply text to place into the current Upwork room composer")] string replyText,
                    [Description("When true, attempt to click Send after inserting the draft. Use only after explicit user confirmation.")] bool sendNow = false) =>
                    await webBrowserService.ReplyInUpworkCurrentRoomAsync(replyText, sendNow),
                "upwork_reply_current_room",
                "Insert a reply into the current Upwork room composer, and optionally send it after explicit confirmation. In draft mode, the reply text is also copied to clipboard."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search for across Voice Admin launcher Name, CommandLine, and CategoryName")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null,
                    [Description("When true, return a Telegram-friendly HTML table (preformatted text).") ] bool htmlFormat = false) =>
                    await voiceAdminService.SearchLauncherEntriesAsync(keyword, maxResults, htmlFormat),
                "search_voice_admin_launchers",
                "Search Voice Admin launcher records by keyword. Returns matching launcher entries with their ID, name, command line, arguments, and category. Use this before launching so you have the correct launcher ID. Set htmlFormat=true for Telegram table-style output."),
            AIFunctionFactory.Create(
                async ([Description("Numeric ID of the launcher to start, obtained from a prior search_voice_admin_launchers call")] int launcherId) =>
                    await voiceAdminService.LaunchLauncherByIdAsync(launcherId),
                "launch_voice_admin_launcher",
                "Launch a Voice Admin launcher entry by its numeric ID on the host machine. Always call search_voice_admin_launchers first to confirm the ID unless the user explicitly provides one."),
            AIFunctionFactory.Create(
                async (
                    [Description("Optional project/category filter for open todos (matches the Todos.Project field)")] string? projectOrCategory = null,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null,
                    [Description("When true, return a Telegram-friendly HTML table (preformatted text). Defaults to true for Telegram readability.") ] bool htmlFormat = true) =>
                    await voiceAdminService.ListIncompleteTodosAsync(projectOrCategory, maxResults, htmlFormat),
                "list_voice_admin_open_todos",
                "List Voice Admin Todos that are not completed and not archived. Includes TodoId, title, project/category, priority, and created date. Use projectOrCategory to filter. Returns Telegram preformatted table output by default."),
            AIFunctionFactory.Create(
                async (
                    [Description("Todo title") ] string title,
                    [Description("Optional todo description") ] string? description = null,
                    [Description("Optional project/category label (stored in the Todos.Project field)")] string? projectOrCategory = null,
                    [Description("Optional sort priority (default 0)")] int sortPriority = 0) =>
                    await voiceAdminService.AddTodoAsync(title, description, projectOrCategory, sortPriority),
                "add_voice_admin_todo",
                "Add a new Voice Admin todo item with title and optional description/project-category/priority. New items are created as incomplete and non-archived."),
            AIFunctionFactory.Create(
                async ([Description("Todo ID to mark complete")] int todoId) =>
                    await voiceAdminService.MarkTodoCompleteAsync(todoId),
                "complete_voice_admin_todo",
                "Mark a Voice Admin todo item complete by Todo ID."),
            AIFunctionFactory.Create(
                async (
                    [Description("Todo title or keyword to find the open todo item") ] string titleOrKeyword,
                    [Description("When true, only an exact title match is accepted. Default false for conversational partial matching.")] bool exactMatch = false) =>
                    await voiceAdminService.MarkTodoCompleteByTextAsync(titleOrKeyword, exactMatch),
                "complete_voice_admin_todo_by_text",
                "Conversational shortcut: mark an open Voice Admin todo complete by title or keyword. If multiple matches are found, it returns candidate TodoIds so you can confirm."),
            AIFunctionFactory.Create(
                async (
                    [Description("Todo ID to update") ] int todoId,
                    [Description("Project/category label to assign. Provide empty text to clear.")] string? projectOrCategory = null) =>
                    await voiceAdminService.AssignTodoProjectAsync(todoId, projectOrCategory),
                "assign_voice_admin_todo_project",
                "Assign or clear the project/category value for a Voice Admin todo item by Todo ID. This updates the Todos.Project field."),
            AIFunctionFactory.Create(
                async (
                    [Description("Todo title or keyword to find the open todo item") ] string titleOrKeyword,
                    [Description("Project/category label to assign. Provide empty text to clear.")] string? projectOrCategory = null,
                    [Description("When true, only an exact title match is accepted. Default false for conversational partial matching.")] bool exactMatch = false) =>
                    await voiceAdminService.AssignTodoProjectByTextAsync(titleOrKeyword, projectOrCategory, exactMatch),
                "assign_voice_admin_todo_project_by_text",
                "Conversational shortcut: assign or clear project/category for an open Voice Admin todo by title or keyword. If multiple matches are found, it returns candidate TodoIds so you can confirm."),
            AIFunctionFactory.Create(
                () => genericDatabaseService.ListSources().Count > 0 ? "configured" : "no sources configured",
                "database_registry_status",
                "Check whether any generic database sources are configured and report status."),
            AIFunctionFactory.Create(
                async ([Description("Optional database alias to select")] string? alias = null) =>
                {
                    if (string.IsNullOrWhiteSpace(alias))
                    {
                        var available = genericDatabaseService.ListSources().Select(s => s.Alias).ToList();
                        return available.Any()
                            ? "Please choose a database alias from: " + string.Join(", ", available)
                            : "No configured databases available.";
                    }

                    var normalized = alias.Trim();
                    if (genericDatabaseService.TryGetSource(normalized, out var source))
                    {
                        return $"Database '{source.Alias}' selected (provider={source.ProviderType}, readOnly={source.ReadOnly}). Use this alias in subsequent commands.";
                    }

                    return $"Alias '{normalized}' is not configured. Current aliases: {string.Join(", ", genericDatabaseService.ListSources().Select(s => s.Alias))}";
                },
                "select_database",
                "Validate and select a configured database alias for future database queries."),
            AIFunctionFactory.Create(
                () => string.Join(", ", genericDatabaseService.ListSources().Select(s => s.Alias + " (" + s.ProviderType + ")")),
                "list_databases",
                "List configured generic database sources with their aliases and provider types."),
            AIFunctionFactory.Create(
                async ([Description("Configured database alias")] string alias) =>
                {
                    var tables = await genericDatabaseService.ListTablesAsync(alias);
                    return tables.Any()
                        ? string.Join("\n", tables)
                        : $"No tables found for database alias '{alias}', or alias is invalid.";
                },
                "list_tables",
                "List tables and views in a configured database alias."),
            AIFunctionFactory.Create(
                async ([Description("Configured database alias")] string alias,
                    [Description("Table name; optionally schema.table for SQL Server")] string tableName) =>
                {
                    var columns = await genericDatabaseService.GetTableSchemaAsync(alias, tableName);
                    return columns.Any()
                        ? string.Join("\n", columns.Select(c => $"{c.Name} ({c.DataType}) nullable={c.IsNullable} pk={c.IsPrimaryKey}"))
                        : $"No schema information found for table '{tableName}' in alias '{alias}'.";
                },
                "describe_table_schema",
                "Get table schema including column names, data types, nullability and primary key."),
            AIFunctionFactory.Create(
                async ([Description("Configured database alias")] string alias,
                    [Description("Table name; optionally schema.table for SQL Server")] string tableName) =>
                {
                    var count = await genericDatabaseService.CountRowsAsync(alias, tableName);
                    return $"{count} row(s) in {tableName} (alias {alias}).";
                },
                "count_table_rows",
                "Return number of rows in the named table."),
            AIFunctionFactory.Create(
                async ([Description("Configured database alias")] string alias,
                    [Description("Object name hint (partial or full table name)")] string nameHint) =>
                {
                    var resolved = await genericDatabaseService.ResolveObjectNameAsync(alias, nameHint);
                    return !string.IsNullOrWhiteSpace(resolved)
                        ? $"Resolved table/view name: {resolved} (alias {alias})."
                        : $"Could not resolve table/view name for hint '{nameHint}' in alias '{alias}'.";
                },
                "resolve_table_object",
                "Resolve a requested table or view name to a configured canonical object name for the database alias."),
            AIFunctionFactory.Create(
                async ([Description("Configured database alias")] string alias,
                    [Description("Table name; optionally schema.table for SQL Server")] string tableName,
                    [Description("Maximum number of rows to preview, default 10")] int maxRows = 10) =>
                {
                    var preview = await genericDatabaseService.PreviewRowsAsync(alias, tableName, maxRows);
                    if (!preview.Any())
                    {
                        return $"No preview rows found for table '{tableName}' using alias '{alias}'.";
                    }

                    var lines = new List<string>();
                    var columns = preview.First().Keys.ToArray();
                    lines.Add(string.Join("\t", columns));
                    foreach (var row in preview)
                    {
                        lines.Add(string.Join("\t", columns.Select(c => row[c]?.ToString() ?? "(null)")));
                    }

                    return string.Join("\n", lines);
                },
                "preview_table_rows",
                "Preview up to maxRows from a table in a configured database."),
            AIFunctionFactory.Create(
                async (
                    [Description("Configured database alias")] string alias,
                    [Description("Table name; optionally schema.table for SQL Server")] string tableName,
                    [Description("Optional WHERE clause without the WHERE keyword")] string? whereClause = null,
                    [Description("Maximum number of rows to return, default 50")] int maxRows = 50) =>
                {
                    var rows = await genericDatabaseService.QueryTableAsync(alias, tableName, whereClause, maxRows);
                    if (!rows.Any())
                    {
                        return $"No rows returned for {tableName} with alias {alias}.";
                    }

                    var columns = rows.First().Keys.ToArray();
                    var lines = new List<string> { string.Join("\t", columns) };
                    foreach (var row in rows)
                    {
                        lines.Add(string.Join("\t", columns.Select(c => row[c]?.ToString() ?? "(null)")));
                    }

                    return string.Join("\n", lines);
                },
                "query_table_rows",
                "Run a read-only filtered SELECT query on a table (with optional WHERE clause)."),
            AIFunctionFactory.Create(
                async (
                    [Description("Configured database alias")] string alias,
                    [Description("Read-only SQL statement (SELECT or WITH, no writes)")] string sql,
                    [Description("Maximum number of rows to return, default 100")] int maxRows = 100) =>
                {
                    if (!SqlSecurityHelper.IsSelectQueryOnly(sql))
                    {
                        return "SQL rejected: only SELECT/with read-only queries are allowed.";
                    }

                    var rows = await genericDatabaseService.ExecuteReadOnlySqlAsync(alias, sql, maxRows);
                    if (!rows.Any())
                    {
                        return "No rows returned (or alias/sql invalid).";
                    }

                    var columns = rows.First().Keys.ToArray();
                    var lines = new List<string> { string.Join("\t", columns) };
                    foreach (var row in rows)
                    {
                        lines.Add(string.Join("\t", columns.Select(c => row[c]?.ToString() ?? "(null)")));
                    }

                    return string.Join("\n", lines);
                },
                "execute_read_only_sql",
                "Execute a read-only SQL statement against the selected database alias (SELECT-only)."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search in Talon Commands table")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null,
                    [Description("When true, return a Telegram-friendly HTML table (preformatted text).") ] bool htmlFormat = false) =>
                    await voiceAdminSearchService.SearchTalonCommandsAsync(keyword, maxResults, htmlFormat),
                "search_talon_commands",
                "Read-only search in the Talon Commands table. Use this when the user asks to list or find Talon command records. Set htmlFormat=true for Telegram table-style output."),
            AIFunctionFactory.Create(
                async ([Description("RowId returned by search_talon_commands")] long rowId) =>
                    await voiceAdminSearchService.GetTalonCommandDetailsByRowIdAsync(rowId),
                "get_talon_command_details",
                "Read full Talon command details by RowId, including script/action logic and related metadata such as application and file path."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search in Custom  intellisense table (snippets)")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null,
                    [Description("When true, return a Telegram-friendly HTML table (preformatted text).") ] bool htmlFormat = false) =>
                    await voiceAdminSearchService.SearchCustomInTeleSenseAsync(keyword, maxResults, htmlFormat),
                "search_custom_in_tele_sense",
                "Read-only search in the Custom intelsense table (snippets). Use this when the user asks to list or find custom intellisense records (snippets). Set htmlFormat=true for Telegram table-style output."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search in Values table")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null,
                    [Description("When true, return a Telegram-friendly HTML table (preformatted text).") ] bool htmlFormat = false) =>
                    await voiceAdminSearchService.SearchValuesAsync(keyword, maxResults, htmlFormat),
                "search_values_records",
                "Read-only search in the Values table. Use this when the user asks to list or find values records. Set htmlFormat=true for Telegram table-style output."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search in Transactions table")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null,
                    [Description("When true, return a Telegram-friendly HTML table (preformatted text).") ] bool htmlFormat = false) =>
                    await voiceAdminSearchService.SearchTransactionsAsync(keyword, maxResults, htmlFormat),
                "search_transactions_records",
                "Read-only search in the Transactions table. Use this when the user asks to list or find transaction records. Set htmlFormat=true for Telegram table-style output."),
            AIFunctionFactory.Create(
                async (
                    [Description("Target table name (for example: Talon Commands, Custom in Tele Sense, Values, or Transactions)")] string tableName,
                    [Description("RowId returned by one of the search_* tools")] long rowId,
                    [Description("Exact column name to copy from the selected row")] string columnName) =>
                {
                    var cellRead = await voiceAdminSearchService.ReadCellValueAsync(tableName, rowId, columnName);
                    if (!cellRead.Success || string.IsNullOrWhiteSpace(cellRead.Value))
                    {
                        return cellRead.Message;
                    }

                    var clipboardResult = await clipboardService.SetClipboardTextForAssistantAsync(cellRead.Value);
                    return $"{clipboardResult} Source: {cellRead.Table}.{cellRead.Column} RowId {cellRead.RowId}.";
                },
                "copy_voice_admin_value_to_clipboard",
                "Read a single value from a Voice Admin table row and copy it to clipboard. This is read-only against the database and only writes to local clipboard when requested by the user."),
            AIFunctionFactory.Create(
                () => talonUserDirectoryService.GetSetupStatusText(),
                "talon_user_directory_status",
                "Check whether Talon user-directory read-only file access is configured and available."),
            AIFunctionFactory.Create(
                async (
                    [Description("Optional relative subdirectory inside Talon user root. Leave empty for full root.")] string? relativePath = null,
                    [Description("File search pattern (default *)")] string searchPattern = "*",
                    [Description("When true (default), include subdirectories recursively.")] bool recursive = true,
                    [Description("Maximum number of files to return (1-1000)")] int maxResults = 200) =>
                    await talonUserDirectoryService.ListFilesAsync(relativePath, searchPattern, recursive, maxResults),
                "list_talon_user_files",
                "List files from the Talon user directory in read-only mode. Returns relative file paths and never writes files."),
            AIFunctionFactory.Create(
                async (
                    [Description("Relative file path inside Talon user directory root")] string relativeFilePath,
                    [Description("Maximum number of characters to return (200-50000)")] int maxChars = 12000) =>
                    await talonUserDirectoryService.ReadFileAsync(relativeFilePath, maxChars),
                "read_talon_user_file",
                "Read the content of a file from the Talon user directory in read-only mode."),
            AIFunctionFactory.Create(
                async (
                    [Description("Text to search for (case-insensitive)")] string query,
                    [Description("Optional relative subdirectory inside Talon user root")] string? relativePath = null,
                    [Description("File search pattern (default *)")] string searchPattern = "*",
                    [Description("When true (default), include subdirectories recursively")] bool recursive = true,
                    [Description("Maximum number of matches to return (1-1000)")] int maxResults = 100) =>
                    await talonUserDirectoryService.SearchTextAsync(query, relativePath, searchPattern, recursive, maxResults),
                "search_talon_user_files_text",
                "Search for text in Talon user directory files in read-only mode."),
            AIFunctionFactory.Create(
                async ([Description("Optional relative subdirectory inside Talon user root. Leave empty to open the Talon root.")] string? relativePath = null) =>
                    await talonUserDirectoryService.OpenInExplorerAsync(relativePath),
                "open_talon_user_directory_in_explorer",
                "Open Windows File Explorer at the Talon user directory (or an optional relative subdirectory) on the host machine."),
            AIFunctionFactory.Create(
                () => knownFolderExplorerService.GetSetupStatusText(),
                "known_folder_explorer_status",
                "Show the configured allowlisted folders for Explorer open actions, including Documents, Desktop, Downloads, Pictures, Videos, and repo."),
            AIFunctionFactory.Create(
                async (
                    [Description("Folder alias to open. Allowed values: documents, desktop, downloads, pictures, videos, repo")] string folderAlias,
                    [Description("Optional relative subdirectory inside the selected folder root")] string? relativePath = null) =>
                    await knownFolderExplorerService.OpenInExplorerAsync(folderAlias, relativePath),
                "open_known_folder_in_explorer",
                "Open Windows File Explorer at an allowlisted folder root (documents, desktop, downloads, pictures, videos, repo) or an optional relative subdirectory inside that root."),
            AIFunctionFactory.Create(
                async (
                    [Description("Folder alias containing the file. Allowed values: documents, desktop, downloads, pictures, videos, repo")] string folderAlias,
                    [Description("Relative file path inside the selected folder root (for example: PersonalityProfile.cs or subfolder/file.txt)")] string relativeFilePath) =>
                    await knownFolderExplorerService.OpenFileInVsCodeAsync(folderAlias, relativeFilePath),
                "open_file_in_vscode",
                "Open a specific file in Visual Studio Code on the host machine. Use the folder alias and a relative path to identify the file. Always use this tool when the user asks to open, view, or edit a file in VS Code or in the editor."),
            AIFunctionFactory.Create(
                () => podcastSubscriptionsService.ListAllSubscriptions(),
                "list_subscribed_podcasts",
                "List all subscribed podcast shows with their search terms. Returns podcast names that can be used with play_podcast_episode."),
            AIFunctionFactory.Create(
                async (
                    [Description("Exact podcast name from list_subscribed_podcasts")] string podcastName,
                    [Description("Episode number counting from latest (1=most recent, 2=second latest, etc). Default 1")] int episodeNumber = 1) =>
                    await PlayPodcastEpisodeAsync(podcastName, episodeNumber, podcastSubscriptionsService, webBrowserService),
                "play_podcast_episode",
                "Play a specific episode of a subscribed podcast. Use list_subscribed_podcasts first to get valid names. Episode 1 is the latest. Podcast playback prefers YouTube Music URLs and falls back to YouTube if needed."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search for in clipboard history")] string keyword,
                    [Description("Maximum number of results to return (1-50)")] int? maxResults = null,
                    [Description("When true, return Telegram-compatible preformatted table text (<pre>). Telegram does not support true <table> tags.") ] bool htmlFormat = false) =>
                    await clipboardHistoryService.SearchAsync(keyword, CancellationToken.None, htmlFormat),
                "search_clipboard_history",
                "Search the clipboard history for entries matching a keyword. Returns entries with timestamps and truncated content snippets from the last 21 days. Set htmlFormat=true for Telegram preformatted table-style output."),
            AIFunctionFactory.Create(
                async ([Description("When true, return Telegram-compatible preformatted table text (<pre>). Telegram does not support true <table> tags.") ] bool htmlFormat = false) =>
                    await clipboardHistoryService.GetTodayEntriesAsync(CancellationToken.None, htmlFormat),
                "get_clipboard_history_today",
                "Get all clipboard history entries recorded today. Shows timestamps and content snippets. Includes both assistant-copied and manually-monitored entries. Set htmlFormat=true for Telegram preformatted table-style output.")
        ];
    }

    private static async Task<string> PlayPodcastEpisodeAsync(
        string podcastName,
        int episodeNumber,
        PodcastSubscriptionsService podcastSubscriptionsService,
        WebBrowserAssistantService webBrowserService)
    {
        if (episodeNumber < 1 || episodeNumber > 100)
        {
            return "Episode number must be between 1 and 100.";
        }

        var subscription = podcastSubscriptionsService.ResolveSubscription(podcastName);
        if (subscription == null)
        {
            var availableList = podcastSubscriptionsService.ListAllSubscriptions();
            return $"Podcast '{podcastName}' not found.\n\n{availableList}";
        }

        if (!string.IsNullOrWhiteSpace(subscription.DirectUrl))
        {
            if (episodeNumber == 1 && IsLikelyYouTubeChannelUrl(subscription.DirectUrl))
            {
                return await webBrowserService.PlayLatestFromYouTubeChannelAsync(subscription.DirectUrl);
            }

            return await webBrowserService.OpenInDefaultBrowserAsync(subscription.DirectUrl);
        }

        var searchQuery = episodeNumber == 1
            ? $"{subscription.Name} {subscription.SearchTerm}"
            : $"{subscription.Name} {subscription.SearchTerm} episode {episodeNumber}";

        return await webBrowserService.PlayTopYouTubeResultAsync(searchQuery, podcastMode: true);
    }

    private static bool IsLikelyYouTubeChannelUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath ?? string.Empty;
        return path.StartsWith("/@", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/c/", StringComparison.OrdinalIgnoreCase);
    }
}
