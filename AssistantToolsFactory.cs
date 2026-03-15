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
        VoiceAdminSearchService voiceAdminSearchService)
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
                async ([Description("Full URL to open in the default browser on the host machine") ] string url) =>
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
                "Find and play the top current YouTube podcast-style result for a topic in the host machine default browser with autoplay."),
            AIFunctionFactory.Create(
                async ([Description("Search query (e.g. Blazor developer jobs Upwork)")] string query) =>
                    await webBrowserService.SearchWebAsync(query),
                "search_web",
                "Search the web using Bing and return the results. Use this to look up information, find jobs, news, or anything else on the internet."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search for across Voice Admin launcher Name, CommandLine, and CategoryName")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null) =>
                    await voiceAdminService.SearchLauncherEntriesAsync(keyword, maxResults),
                "search_voice_admin_launchers",
                "Search Voice Admin launcher records by keyword. Returns matching launcher entries with their ID, name, command line, arguments, and category. Use this before launching so you have the correct launcher ID."),
            AIFunctionFactory.Create(
                async ([Description("Numeric ID of the launcher to start, obtained from a prior search_voice_admin_launchers call")] int launcherId) =>
                    await voiceAdminService.LaunchLauncherByIdAsync(launcherId),
                "launch_voice_admin_launcher",
                "Launch a Voice Admin launcher entry by its numeric ID on the host machine. Always call search_voice_admin_launchers first to confirm the ID unless the user explicitly provides one."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search in Talon Commands table")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null) =>
                    await voiceAdminSearchService.SearchTalonCommandsAsync(keyword, maxResults),
                "search_talon_commands",
                "Read-only search in the Talon Commands table. Use this when the user asks to list or find Talon command records."),
            AIFunctionFactory.Create(
                async ([Description("RowId returned by search_talon_commands")] long rowId) =>
                    await voiceAdminSearchService.GetTalonCommandDetailsByRowIdAsync(rowId),
                "get_talon_command_details",
                "Read full Talon command details by RowId, including script/action logic and related metadata such as application and file path."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search in Custom in Tele Sense table")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null) =>
                    await voiceAdminSearchService.SearchCustomInTeleSenseAsync(keyword, maxResults),
                "search_custom_in_tele_sense",
                "Read-only search in the Custom in Tele Sense table. Use this when the user asks to list or find custom tele-sense records."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search in Values table")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null) =>
                    await voiceAdminSearchService.SearchValuesAsync(keyword, maxResults),
                "search_values_records",
                "Read-only search in the Values table. Use this when the user asks to list or find values records."),
            AIFunctionFactory.Create(
                async (
                    [Description("Keyword to search in Transactions table")] string keyword,
                    [Description("Maximum number of results to return (1-100, default 20)")] int? maxResults = null) =>
                    await voiceAdminSearchService.SearchTransactionsAsync(keyword, maxResults),
                "search_transactions_records",
                "Read-only search in the Transactions table. Use this when the user asks to list or find transaction records."),
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
                "Read a single value from a Voice Admin table row and copy it to clipboard. This is read-only against the database and only writes to local clipboard when requested by the user.")
        ];
    }
}
