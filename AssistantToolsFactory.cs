using System.ComponentModel;
using Microsoft.Extensions.AI;

internal static class AssistantToolsFactory
{
    public static List<AIFunction> Build(
        GmailAssistantService gmailService,
        GoogleCalendarAssistantService calendarService,
        NaturalCommandsAssistantService naturalCommandsService,
        ClipboardAssistantService clipboardService,
        WebBrowserAssistantService webBrowserService,
        VoiceAdminService voiceAdminService,
        VoiceAdminSearchService voiceAdminSearchService,
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
