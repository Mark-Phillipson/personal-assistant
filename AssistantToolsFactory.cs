using System.ComponentModel;
using Microsoft.Extensions.AI;

internal static class AssistantToolsFactory
{
    public static List<AIFunction> Build(
        GmailAssistantService gmailService,
        GoogleCalendarAssistantService calendarService,
        NaturalCommandsAssistantService naturalCommandsService)
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
                    [Description("Maximum number of unread inbox emails to return (1-20)")] int maxResults = 5,
                    [Description("Optional Gmail query filter (for example: from:amazon newer_than:7d)")] string? query = null) =>
                    await gmailService.ListUnreadMessagesAsync(maxResults, query),
                "list_unread_gmail",
                "List unread inbox messages from Gmail with subject, sender, date, and snippet."),
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
                "Run a local NaturalCommands command on the machine hosting this bot.")
        ];
    }
}
