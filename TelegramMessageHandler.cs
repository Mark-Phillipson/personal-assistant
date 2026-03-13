using System.Collections.Concurrent;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

internal static class TelegramMessageHandler
{
    public static async Task HandleAsync(
        TelegramMessage message,
        string text,
        TelegramApiClient telegram,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools,
        GmailAssistantService gmailService,
        GoogleCalendarAssistantService calendarService,
        NaturalCommandsAssistantService naturalCommandsService,
        CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        if (text.StartsWith('/'))
        {
            var command = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0].ToLowerInvariant();
            switch (command)
            {
                case "/start":
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        "Hi! I am your Copilot SDK personal assistant.\n\nI can chat and help with your Gmail inbox.\nUse /help for commands.",
                        cancellationToken);
                    return;

                case "/help":
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        "Commands:\n/start - welcome message\n/help - show commands\n/reset - reset your Copilot session\n/gmail-status - check Gmail setup\n/calendar-status - check Google Calendar setup\n/calendar-events - list upcoming Google Calendar events\n/calendar-create - create a new Google Calendar event\n/natural <command> - run a NaturalCommands command locally\n/nc <command> - short alias for /natural",
                        cancellationToken);
                    return;

                case "/gmail-status":
                    await telegram.SendMessageInChunksAsync(chatId, gmailService.GetSetupStatusText(), cancellationToken);
                    return;

                case "/calendar-status":
                    await telegram.SendMessageInChunksAsync(chatId, calendarService.GetSetupStatusText(), cancellationToken);
                    return;

                case "/calendar-events":
                    if (!calendarService.IsConfigured)
                    {
                        await telegram.SendMessageInChunksAsync(chatId, calendarService.GetSetupStatusText(), cancellationToken);
                        return;
                    }

                    var events = await calendarService.ListUpcomingEventsAsync(5);
                    if (events.Count == 0)
                    {
                        await telegram.SendMessageInChunksAsync(chatId, "No upcoming events found.", cancellationToken);
                    }
                    else
                    {
                        var eventList = string.Join("\n\n", events.Select(ev => $"{ev.Summary}\nStart: {ev.Start?.DateTimeDateTimeOffset}\nEnd: {ev.End?.DateTimeDateTimeOffset}"));
                        await telegram.SendMessageInChunksAsync(chatId, eventList, cancellationToken);
                    }

                    return;

                case "/calendar-create":
                    if (!calendarService.IsConfigured)
                    {
                        await telegram.SendMessageInChunksAsync(chatId, calendarService.GetSetupStatusText(), cancellationToken);
                        return;
                    }

                    await telegram.SendMessageInChunksAsync(chatId, "To create an event, please use the assistant chat with a command like: 'Create a calendar event titled Meeting tomorrow at 10am for 1 hour.'", cancellationToken);
                    return;

                case "/reset":
                    if (sessions.TryRemove(chatId, out var removedSession))
                    {
                        await removedSession.DisposeAsync();
                    }

                    await telegram.SendMessageInChunksAsync(chatId, "Session reset. Start a new conversation anytime.", cancellationToken);
                    return;

                case "/natural":
                case "/nc":
                    var commandPayload = ExtractCommandPayload(text);
                    var naturalCommandResult = await naturalCommandsService.ExecuteAsync(commandPayload, cancellationToken);
                    await telegram.SendMessageInChunksAsync(chatId, naturalCommandResult.Message, cancellationToken);
                    return;

                default:
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        "Unknown command. Use /help to see available commands.",
                        cancellationToken);
                    return;
            }
        }

        var session = await GetOrCreateSessionAsync(chatId, copilotClient, sessions, assistantTools);

        try
        {
            var assistantReply = await session.SendAndWaitAsync(new MessageOptions { Prompt = text });
            var content = assistantReply?.Data.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                content = "I could not generate a response. Please try again.";
            }

            await telegram.SendMessageInChunksAsync(chatId, content, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[copilot.session.error] chat={chatId} {ex.Message}");
            await telegram.SendMessageInChunksAsync(
                chatId,
                "I hit an error while generating a reply. Please try again.",
                cancellationToken);
        }
    }

    private static async Task<CopilotSession> GetOrCreateSessionAsync(
        long chatId,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools)
    {
        if (sessions.TryGetValue(chatId, out var existingSession))
        {
            return existingSession;
        }

        var createdSession = await copilotClient.CreateSessionAsync(new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = assistantTools
        });

        if (sessions.TryAdd(chatId, createdSession))
        {
            return createdSession;
        }

        await createdSession.DisposeAsync();
        return sessions[chatId];
    }

    private static string ExtractCommandPayload(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        return parts.Length < 2 ? string.Empty : parts[1];
    }
}
