using System.Collections.Concurrent;
using System.Diagnostics;
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
        ConcurrentDictionary<long, PersonalityProfile> personalityProfiles,
        ICollection<AIFunction> assistantTools,
        PersonalityProfile defaultPersonality,
        PersonalityProfile environmentPersonality,
        GmailAssistantService gmailService,
        GoogleCalendarAssistantService calendarService,
        NaturalCommandsAssistantService naturalCommandsService,
        ClipboardAssistantService clipboardService,
        CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var profile = GetPersonalityForChat(chatId, personalityProfiles, defaultPersonality);

        if (text.StartsWith('/'))
        {
            var command = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0].ToLowerInvariant();
            switch (command)
            {
                case "/start":
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        string.Join("\n\n", new[]
                        {
                            EmojiPalette.Wrap($"Hi! I'm {profile.Name}, your Copilot SDK personal assistant.", EmojiPalette.Wave, profile.UseEmoji),
                            EmojiPalette.Wrap("I can chat and help with Gmail and Google Calendar.", EmojiPalette.Happy, profile.UseEmoji),
                            "Use /help for commands."
                        }),
                        cancellationToken);
                    return;

                case "/help":
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        BuildHelpText(profile),
                        cancellationToken);
                    return;

                case "/gmail-status":
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap(gmailService.GetSetupStatusText(), EmojiPalette.Email, profile.UseEmoji),
                        cancellationToken);
                    return;

                case "/calendar-status":
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap(calendarService.GetSetupStatusText(), EmojiPalette.Calendar, profile.UseEmoji),
                        cancellationToken);
                    return;

                case "/clipboard-status":
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap(clipboardService.GetSetupStatusText(), EmojiPalette.Confirm, profile.UseEmoji),
                        cancellationToken);
                    return;

                case "/calendar-events":
                    if (!calendarService.IsConfigured)
                    {
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap(calendarService.GetSetupStatusText(), EmojiPalette.Warning, profile.UseEmoji),
                            cancellationToken);
                        return;
                    }

                    var events = await calendarService.ListUpcomingEventsAsync(5);
                    if (events.Count == 0)
                    {
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap("No upcoming events found.", EmojiPalette.Calendar, profile.UseEmoji),
                            cancellationToken);
                    }
                    else
                    {
                        var eventList = string.Join("\n\n", events.Select(ev => $"{ev.Summary}\nStart: {ev.Start?.DateTimeDateTimeOffset}\nEnd: {ev.End?.DateTimeDateTimeOffset}"));
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap(eventList, EmojiPalette.Calendar, profile.UseEmoji),
                            cancellationToken);
                    }

                    return;

                case "/calendar-create":
                    if (!calendarService.IsConfigured)
                    {
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap(calendarService.GetSetupStatusText(), EmojiPalette.Warning, profile.UseEmoji),
                            cancellationToken);
                        return;
                    }

                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap("To create an event, please use the assistant chat with a command like: 'Create a calendar event titled Meeting tomorrow at 10am for 1 hour.'", EmojiPalette.Calendar, profile.UseEmoji),
                        cancellationToken);
                    return;

                case "/weather":
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap("Opening BBC Weather for Maidstone, Kent...", EmojiPalette.Search, profile.UseEmoji),
                        cancellationToken);

                    var weatherUrl = "https://www.bbc.co.uk/weather/2643179";
                    try
                    {
                        _ = Process.Start(new ProcessStartInfo
                        {
                            FileName = weatherUrl,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // Fall back to NaturalCommands if direct browser launch is unavailable.
                        await naturalCommandsService.ExecuteAsync($"open {weatherUrl}", cancellationToken);
                    }

                    return;

                case "/reset":
                    if (sessions.TryRemove(chatId, out var removedSession))
                    {
                        await removedSession.DisposeAsync();
                    }

                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap("Session reset. Start a new conversation anytime.", EmojiPalette.Confirm, profile.UseEmoji),
                        cancellationToken);
                    return;

                case "/personality":
                    await HandlePersonalityCommandAsync(
                        chatId,
                        text,
                        telegram,
                        sessions,
                        personalityProfiles,
                        defaultPersonality,
                        environmentPersonality,
                        cancellationToken);
                    return;

                case "/natural":
                case "/nc":
                    var commandPayload = ExtractCommandPayload(text);
                    var naturalCommandResult = await naturalCommandsService.ExecuteAsync(commandPayload, cancellationToken);
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap(naturalCommandResult.Message, EmojiPalette.Rocket, profile.UseEmoji),
                        cancellationToken);
                    return;

                default:
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap("Unknown command. Use /help to see available commands.", EmojiPalette.Warning, profile.UseEmoji),
                        cancellationToken);
                    return;
            }
        }

        var session = await GetOrCreateSessionAsync(chatId, copilotClient, sessions, assistantTools, profile);

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
                EmojiPalette.Wrap("I hit an error while generating a reply. Please try again.", EmojiPalette.Warning, profile.UseEmoji),
                cancellationToken);
        }
    }

    private static async Task<CopilotSession> GetOrCreateSessionAsync(
        long chatId,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools,
        PersonalityProfile profile)
    {
        if (sessions.TryGetValue(chatId, out var existingSession))
        {
            return existingSession;
        }

        var createdSession = await copilotClient.CreateSessionAsync(new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = assistantTools,
            SystemMessage = new SystemMessageConfig
            {
                Content = SystemPromptBuilder.Build(profile)
            }
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

    private static PersonalityProfile GetPersonalityForChat(
        long chatId,
        ConcurrentDictionary<long, PersonalityProfile> personalityProfiles,
        PersonalityProfile defaultPersonality)
    {
        return personalityProfiles.TryGetValue(chatId, out var profile)
            ? profile
            : defaultPersonality;
    }

    private static string BuildHelpText(PersonalityProfile profile)
    {
        var commandRows = new[]
        {
            FormatCommandLine("/start", "welcome message", EmojiPalette.Wave, profile.UseEmoji),
            FormatCommandLine("/help", "show commands", EmojiPalette.Commands, profile.UseEmoji),
            FormatCommandLine("/reset", "reset your Copilot session", EmojiPalette.Confirm, profile.UseEmoji),
            FormatCommandLine("/gmail-status", "check Gmail setup", EmojiPalette.Email, profile.UseEmoji),
            FormatCommandLine("/calendar-status", "check Google Calendar setup", EmojiPalette.Calendar, profile.UseEmoji),
            FormatCommandLine("/clipboard-status", "check clipboard setup", EmojiPalette.Confirm, profile.UseEmoji),
            FormatCommandLine("/calendar-events", "list upcoming Google Calendar events", EmojiPalette.Calendar, profile.UseEmoji),
            FormatCommandLine("/calendar-create", "create a new Google Calendar event", EmojiPalette.Calendar, profile.UseEmoji),
            FormatCommandLine("/weather", "open BBC Weather for Maidstone, Kent", EmojiPalette.Search, profile.UseEmoji),
            FormatCommandLine("/natural <command>", "run a NaturalCommands command locally", EmojiPalette.Rocket, profile.UseEmoji),
            FormatCommandLine("/nc <command>", "short alias for /natural", EmojiPalette.Rocket, profile.UseEmoji),
            FormatCommandLine("/personality ...", "adjust tone and emoji settings", EmojiPalette.Personality, profile.UseEmoji)
        };

        var personalityStatus = GetPersonalityStatusText(profile);
        var commandHeader = EmojiPalette.Wrap("Commands:", EmojiPalette.Commands, profile.UseEmoji);

        return string.Join("\n", new[]
        {
            EmojiPalette.Wrap(personalityStatus, EmojiPalette.Personality, profile.UseEmoji),
            string.Empty,
            commandHeader,
            string.Join("\n", commandRows),
            string.Empty,
            "Personality quick controls:",
            "/personality emoji on|off|subtle|moderate|expressive",
            "/personality tone friendly|professional|witty|calm|irreverent",
            "/personality reset"
        });
    }

    private static async Task HandlePersonalityCommandAsync(
        long chatId,
        string text,
        TelegramApiClient telegram,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ConcurrentDictionary<long, PersonalityProfile> personalityProfiles,
        PersonalityProfile defaultPersonality,
        PersonalityProfile environmentPersonality,
        CancellationToken cancellationToken)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = GetPersonalityForChat(chatId, personalityProfiles, defaultPersonality);

        if (parts.Length < 2)
        {
            await telegram.SendMessageInChunksAsync(
                chatId,
                BuildPersonalityUsageText(current),
                cancellationToken);
            return;
        }

        var section = parts[1].ToLowerInvariant();
        PersonalityProfile updated;
        string confirmation;

        switch (section)
        {
            case "reset":
                updated = environmentPersonality;
                personalityProfiles[chatId] = updated;
                confirmation = "Personality reset to environment defaults.";
                break;

            case "emoji":
                if (parts.Length < 3)
                {
                    await telegram.SendMessageInChunksAsync(chatId, BuildPersonalityUsageText(current), cancellationToken);
                    return;
                }

                var emojiSetting = parts[2].ToLowerInvariant();
                switch (emojiSetting)
                {
                    case "on":
                        updated = current with { UseEmoji = true };
                        confirmation = "Emoji enabled.";
                        break;
                    case "off":
                        updated = current with { UseEmoji = false };
                        confirmation = "Emoji disabled.";
                        break;
                    case "subtle":
                        updated = current with { UseEmoji = true, EmojiDensity = EmojiDensity.Subtle };
                        confirmation = "Emoji density set to Subtle.";
                        break;
                    case "moderate":
                        updated = current with { UseEmoji = true, EmojiDensity = EmojiDensity.Moderate };
                        confirmation = "Emoji density set to Moderate.";
                        break;
                    case "expressive":
                        updated = current with { UseEmoji = true, EmojiDensity = EmojiDensity.Expressive };
                        confirmation = "Emoji density set to Expressive.";
                        break;
                    default:
                        await telegram.SendMessageInChunksAsync(chatId, BuildPersonalityUsageText(current), cancellationToken);
                        return;
                }

                personalityProfiles[chatId] = updated;
                break;

            case "tone":
                if (parts.Length < 3 || !Enum.TryParse<AssistantTone>(parts[2], ignoreCase: true, out var tone))
                {
                    await telegram.SendMessageInChunksAsync(chatId, BuildPersonalityUsageText(current), cancellationToken);
                    return;
                }

                updated = current with { Tone = tone };
                personalityProfiles[chatId] = updated;
                confirmation = $"Tone set to {tone}.";
                break;

            default:
                await telegram.SendMessageInChunksAsync(chatId, BuildPersonalityUsageText(current), cancellationToken);
                return;
        }

        if (sessions.TryRemove(chatId, out var removedSession))
        {
            await removedSession.DisposeAsync();
        }

        await telegram.SendMessageInChunksAsync(
            chatId,
            string.Join("\n", new[]
            {
                EmojiPalette.Wrap(confirmation, EmojiPalette.Confirm, updated.UseEmoji),
                EmojiPalette.Wrap(GetPersonalityStatusText(updated), EmojiPalette.Personality, updated.UseEmoji)
            }),
            cancellationToken);
    }

    private static string BuildPersonalityUsageText(PersonalityProfile profile)
    {
        return string.Join("\n", new[]
        {
            EmojiPalette.Wrap("Usage:", EmojiPalette.Personality, profile.UseEmoji),
            "/personality emoji on",
            "/personality emoji off",
            "/personality emoji subtle",
            "/personality emoji expressive",
            "/personality tone witty",
            "/personality reset"
        });
    }

    private static string GetPersonalityStatusText(PersonalityProfile profile)
    {
        var emojiState = profile.UseEmoji ? profile.EmojiDensity.ToString() : "Off";
        return $"Personality: {profile.Name} | Tone: {profile.Tone} | Emoji: {emojiState}";
    }

    private static string FormatCommandLine(string command, string description, string emoji, bool useEmoji)
    {
        return $"{EmojiPrefix(emoji, useEmoji)}{command} - {description}";
    }

    private static string EmojiPrefix(string emoji, bool useEmoji)
    {
        return useEmoji ? $"{emoji} " : "- ";
    }
}
