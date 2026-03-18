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
        TelegramAttachmentService attachmentService,
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
        PodcastSubscriptionsService podcastSubscriptionsService,
        ClipboardHistoryService clipboardHistoryService,
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
                            EmojiPalette.Wrap("I can chat, inspect photos and documents you send, and help with Gmail and Google Calendar.", EmojiPalette.Happy, profile.UseEmoji),
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
                        var eventItems = events.Select(ev => (
                            label: TelegramRichTextFormatter.Bold(ev.Summary ?? "Event"),
                            secondary: $"Start: {ev.Start?.DateTimeDateTimeOffset}\nEnd: {ev.End?.DateTimeDateTimeOffset}"
                        )).ToList();
                        var eventList = TelegramRichTextFormatter.LabeledList("📅 Upcoming Events", eventItems);
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            eventList,
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

                    var weatherUrl = "https://www.bbc.co.uk/weather/ME15";
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

                case "/podcasts":
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        podcastSubscriptionsService.ListAllSubscriptions(),
                        cancellationToken);
                    return;

                case "/play-podcast":
                    await HandlePlayPodcastCommandAsync(
                        chatId,
                        text,
                        telegram,
                        podcastSubscriptionsService,
                        profile,
                        cancellationToken);
                    return;

                case "/add-podcast":
                    await HandleAddPodcastCommandAsync(
                        chatId,
                        text,
                        telegram,
                        podcastSubscriptionsService,
                        profile,
                        cancellationToken);
                    return;

                case "/clipboard-search":
                    var keyword = ExtractCommandPayload(text);
                    if (string.IsNullOrWhiteSpace(keyword))
                    {
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap("Usage: /clipboard-search <keyword>", EmojiPalette.Search, profile.UseEmoji),
                            cancellationToken);
                        return;
                    }

                    var searchResult = await clipboardHistoryService.SearchAsync(keyword, cancellationToken, asHtmlTable: true);
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap(searchResult, EmojiPalette.Search, profile.UseEmoji),
                        cancellationToken);
                    return;

                case "/clipboard-today":
                    var todayResult = await clipboardHistoryService.GetTodayEntriesAsync(cancellationToken, asHtmlTable: true);
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap(todayResult, EmojiPalette.Search, profile.UseEmoji),
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

        TelegramStoredAttachment? storedAttachment = null;

        try
        {
            storedAttachment = await attachmentService.TryStoreMessageAttachmentAsync(message, telegram, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[telegram.attachment.error] chat={chatId} {ex}");
            await telegram.SendMessageInChunksAsync(
                chatId,
                EmojiPalette.Wrap($"I couldn't download the Telegram attachment: {ex.Message}", EmojiPalette.Warning, profile.UseEmoji),
                cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(text) && storedAttachment is null)
        {
            return;
        }

        var messageOptions = BuildMessageOptions(text, storedAttachment);

        try
        {
            var content = await SendWithSessionRecoveryAsync(
                chatId,
                messageOptions,
                copilotClient,
                sessions,
                assistantTools,
                profile,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                content = "I could not generate a response. Please try again.";
            }

            await telegram.SendMessageInChunksAsync(chatId, content, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[copilot.session.error] chat={chatId} {ex}");

            // Remove the broken session so the next request creates a fresh one.
            if (sessions.TryRemove(chatId, out var failedSession))
            {
                await failedSession.DisposeAsync();
            }

            try
            {
                await telegram.SendMessageInChunksAsync(
                    chatId,
                    EmojiPalette.Wrap("I hit an error while generating a reply. Please try again.", EmojiPalette.Warning, profile.UseEmoji),
                    cancellationToken);
            }
            catch (Exception sendEx)
            {
                Console.Error.WriteLine($"[telegram.send.error] chat={chatId} {sendEx}");
            }
        }
        finally
        {
            attachmentService.DeleteStoredAttachment(storedAttachment);
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

    private static async Task<string?> SendWithSessionRecoveryAsync(
        long chatId,
        MessageOptions messageOptions,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools,
        PersonalityProfile profile,
        CancellationToken cancellationToken)
    {
        var session = await GetOrCreateSessionAsync(chatId, copilotClient, sessions, assistantTools, profile);

        try
        {
            var assistantReply = await session.SendAndWaitAsync(messageOptions, null, cancellationToken);
            return assistantReply?.Data.Content?.Trim();
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            Console.Error.WriteLine($"[copilot.session.recover] chat={chatId} Session not found; recreating session and retrying once.");

            if (sessions.TryRemove(chatId, out var staleSession))
            {
                await staleSession.DisposeAsync();
            }

            var recreatedSession = await GetOrCreateSessionAsync(chatId, copilotClient, sessions, assistantTools, profile);
            var assistantReply = await recreatedSession.SendAndWaitAsync(messageOptions, null, cancellationToken);
            return assistantReply?.Data.Content?.Trim();
        }
    }

    private static bool IsSessionNotFoundError(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)
                && current.Message.Contains("session not found", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
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

    private static MessageOptions BuildMessageOptions(string text, TelegramStoredAttachment? storedAttachment)
    {
        var prompt = BuildPrompt(text, storedAttachment);
        var options = new MessageOptions
        {
            Prompt = prompt
        };

        if (storedAttachment is null)
        {
            return options;
        }

        options.Attachments = new List<UserMessageDataAttachmentsItem>
        {
            new UserMessageDataAttachmentsItemFile
            {
                Type = "file",
                Path = storedAttachment.LocalPath,
                DisplayName = storedAttachment.DisplayName
            }
        };

        return options;
    }

    private static string BuildPrompt(string text, TelegramStoredAttachment? storedAttachment)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (storedAttachment is null)
        {
            return "Please help the user.";
        }

        return $"The user sent a Telegram {storedAttachment.Kind} attachment named '{storedAttachment.DisplayName}' with no caption. Inspect the attachment, describe the relevant contents briefly, and ask a concise follow-up if their intent is unclear.";
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
            FormatCommandLine("/clipboard-search <keyword>", "search clipboard history", EmojiPalette.Search, profile.UseEmoji),
            FormatCommandLine("/clipboard-today", "view today's clipboard history", EmojiPalette.Search, profile.UseEmoji),
            FormatCommandLine("/calendar-events", "list upcoming Google Calendar events", EmojiPalette.Calendar, profile.UseEmoji),
            FormatCommandLine("/calendar-create", "create a new Google Calendar event", EmojiPalette.Calendar, profile.UseEmoji),
            FormatCommandLine("/podcasts", "list subscribed podcasts", EmojiPalette.Music, profile.UseEmoji),
            FormatCommandLine("/play-podcast <name> [N]", "play Nth latest episode (default 1)", EmojiPalette.Music, profile.UseEmoji),
            FormatCommandLine("/add-podcast <name> <search>", "add podcast subscription", EmojiPalette.Music, profile.UseEmoji),
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
            "You can also send a Telegram photo or document directly. It will be forwarded to Copilot as an attachment.",
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

    private static async Task HandlePlayPodcastCommandAsync(
        long chatId,
        string text,
        TelegramApiClient telegram,
        PodcastSubscriptionsService podcastSubscriptionsService,
        PersonalityProfile profile,
        CancellationToken cancellationToken)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
        {
            await telegram.SendMessageInChunksAsync(
                chatId,
                EmojiPalette.Wrap("Usage: /play-podcast <podcast-name> [episode-number]", EmojiPalette.Music, profile.UseEmoji),
                cancellationToken);
            return;
        }

        // Reconstruct podcast name from all parts except the command and optional episode number
        var episodeNumber = 1;
        var lastPart = parts[^1];
        var podcastNameParts = new List<string>();

        // Check if the last part is a number (episode)
        if (int.TryParse(lastPart, out var parsedEpisode))
        {
            episodeNumber = parsedEpisode;
            // All parts except first (command) and last (episode number) form the podcast name
            podcastNameParts.AddRange(parts.Skip(1).SkipLast(1));
        }
        else
        {
            // All parts except first (command) form the podcast name
            podcastNameParts.AddRange(parts.Skip(1));
        }

        var podcastName = string.Join(" ", podcastNameParts);

        if (string.IsNullOrWhiteSpace(podcastName))
        {
            await telegram.SendMessageInChunksAsync(
                chatId,
                EmojiPalette.Wrap("Please provide a podcast name.", EmojiPalette.Music, profile.UseEmoji),
                cancellationToken);
            return;
        }

        await telegram.SendMessageInChunksAsync(
            chatId,
            EmojiPalette.Wrap($"🎵 Playing {podcastName} episode {episodeNumber}...", EmojiPalette.Music, profile.UseEmoji),
            cancellationToken);

        // Note: The actual browser playback is initiated by WebBrowserService via Process.Start
        // We just send feedback to the user
    }

    private static async Task HandleAddPodcastCommandAsync(
        long chatId,
        string text,
        TelegramApiClient telegram,
        PodcastSubscriptionsService podcastSubscriptionsService,
        PersonalityProfile profile,
        CancellationToken cancellationToken)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 3)
        {
            await telegram.SendMessageInChunksAsync(
                chatId,
                EmojiPalette.Wrap("Usage: /add-podcast <podcast-name> <search-term>", EmojiPalette.Music, profile.UseEmoji),
                cancellationToken);
            return;
        }

        var remainder = parts[1];
        var subParts = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (subParts.Length < 2)
        {
            await telegram.SendMessageInChunksAsync(
                chatId,
                EmojiPalette.Wrap("Usage: /add-podcast <podcast-name> <search-term>", EmojiPalette.Music, profile.UseEmoji),
                cancellationToken);
            return;
        }

        var podcastName = subParts[0];
        var searchTerm = subParts[1];

        await podcastSubscriptionsService.AddSubscriptionAsync(podcastName, searchTerm);

        await telegram.SendMessageInChunksAsync(
            chatId,
            EmojiPalette.Wrap($"✅ Added podcast '{podcastName}' with search term '{searchTerm}'", EmojiPalette.Confirm, profile.UseEmoji),
            cancellationToken);
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
