using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

internal static class TelegramMessageHandler
{
    private static readonly ConcurrentDictionary<long, string> SessionToolSignatures = new();
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
        TickerNotificationService tickerNotificationService,
        DadJokeService dadJokeService,
        WebBrowserAssistantService webBrowserService,
        VoiceAdminService voiceAdminService,
        GitHubTodosService gitHubTodosService,
        PodcastSubscriptionsService podcastSubscriptionsService,
        ClipboardHistoryService clipboardHistoryService,
        TextToSpeechService textToSpeechService,
        PronunciationDictionaryService pronunciationService,
        KnownFolderExplorerService knownFolderExplorerService,
        ConcurrentDictionary<long, List<string>> conversationHistories,
        CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var profile = GetPersonalityForChat(chatId, personalityProfiles, defaultPersonality);

        // Redact attempts to read or display .env or secrets: replace values with [REDACTED]
        if (!string.IsNullOrWhiteSpace(text) && Regex.IsMatch(text, @"(\.|\b)(env|\.env|env file|show\s+env|show\s+\.env|display\s+env)\b", RegexOptions.IgnoreCase))
        {
            try
            {
                // Look for .env in common locations
                var envPaths = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, ".env"),
                    Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                    ".env"
                };

                string? found = null;
                foreach (var p in envPaths)
                {
                    if (File.Exists(p))
                    {
                        found = p;
                        break;
                    }
                }

                if (found is null)
                {
                    await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap("I will not expose secrets. No .env file was accessible.", EmojiPalette.Warning, profile.UseEmoji), cancellationToken);
                    return;
                }

                var lines = await File.ReadAllLinesAsync(found, cancellationToken);
                var secretKeyPattern = new Regex(@"\b(KEY|TOKEN|SECRET|PASSWORD|CLIENT_SECRET|CLIENT_ID)\b", RegexOptions.IgnoreCase);
                var redactExactKeys = new Regex(@"^(ASSISTANT_MODEL|ANDROID_DEVICE_TOKEN|TELEGRAM_BOT_TOKEN|AZURE_SPEECH_KEY|GMAIL_CLIENT_SECRET_PATH|CALENDAR_CLIENT_SECRET_PATH|NATURAL_COMMANDS_EXECUTABLE|VOICE_LAUNCHER_DB_PATH|VOICE_ADMIN_DB_PATH)\s*=", RegexOptions.IgnoreCase);

                var redacted = lines.Select(l =>
                {
                    if (redactExactKeys.IsMatch(l))
                    {
                        var idx = l.IndexOf('=');
                        if (idx >= 0) return l.Substring(0, idx + 1) + " [REDACTED]";
                    }

                    if (secretKeyPattern.IsMatch(l))
                    {
                        var idx = l.IndexOf('=');
                        if (idx >= 0) return l.Substring(0, idx + 1) + " [REDACTED]";
                    }

                    return l;
                });

                var messageText = "Redacted .env contents:\n" + string.Join("\n", redacted);
                await telegram.SendMessageInChunksAsync(chatId, TelegramRichTextFormatter.CodeBlock(messageText), cancellationToken);
            }
            catch (Exception ex)
            {
                await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap($"I will not expose secrets: {ex.Message}", EmojiPalette.Warning, profile.UseEmoji), cancellationToken);
            }

            return;
        }

        // TTS service isolate route
        if (text.Equals("tts test", StringComparison.OrdinalIgnoreCase) || text.Equals("/tts-test", StringComparison.OrdinalIgnoreCase))
        {
            const string testPhrase = "This is a text to speech service test. If you hear this, Text To Speech is working.";
            await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap("Running TTS test...", EmojiPalette.Rocket, profile.UseEmoji), cancellationToken);

            try
            {
                await textToSpeechService.TrySpeakPreviewAsync(testPhrase, cancellationToken);
                await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap("TTS test completed; check your speaker output.", EmojiPalette.Confirm, profile.UseEmoji), cancellationToken);
            }
            catch (Exception ex)
            {
                await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap($"TTS test failed: {ex.Message}", EmojiPalette.Warning, profile.UseEmoji), cancellationToken);
            }

            return;
        }

        if (TryParsePronunciationAddRequest(text, out var originalWord, out var replacementWord, out var ipa))
        {
            var ssmlPhoneme = BuildSsmlPhoneme(originalWord, ipa);
            var context = ipa is null
                ? "Added from Telegram conversational pronunciation request."
                : $"Added from Telegram conversational pronunciation request. IPA: {ipa}";

            await pronunciationService.AddCorrectionAsync(
                originalWord,
                replacementWord,
                ssmlPhoneme,
                context,
                cancellationToken);

            await telegram.SendMessageInChunksAsync(
                chatId,
                EmojiPalette.Wrap(
                    $"Pronunciation saved: '{originalWord}' -> '{replacementWord}'{(ipa is null ? string.Empty : $" (IPA {ipa})")}. I will use this in future TTS replies.",
                    EmojiPalette.Confirm,
                    profile.UseEmoji),
                cancellationToken);
            return;
        }

        // Voice-friendly natural command syntax (no slash)
        if (text.StartsWith("natural", StringComparison.OrdinalIgnoreCase))
        {
            var commandPayload = text.Length > "natural".Length ? text["natural".Length..].Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(commandPayload))
            {
                await telegram.SendMessageInChunksAsync(
                    chatId,
                    EmojiPalette.Wrap("Usage: natural <command>", EmojiPalette.Warning, profile.UseEmoji),
                    cancellationToken);
                return;
            }

            var naturalResult = await naturalCommandsService.ExecuteAsync(commandPayload, cancellationToken);
            var naturalContent = EmojiPalette.Wrap(naturalResult.Message, EmojiPalette.Rocket, profile.UseEmoji);
            await SendReplyWithOptionalTelegramAudioAsync(
                chatId,
                naturalContent,
                naturalResult.Message,
                text,
                telegram,
                textToSpeechService,
                cancellationToken,
                "natural command audio failed");

            return;
        }

        if (text.StartsWith("nc", StringComparison.OrdinalIgnoreCase))
        {
            var commandPayload = text.Length > 2 ? text[2..].Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(commandPayload))
            {
                await telegram.SendMessageInChunksAsync(
                    chatId,
                    EmojiPalette.Wrap("Usage: nc <your question or command>", EmojiPalette.Warning, profile.UseEmoji),
                    cancellationToken);
                return;
            }

            await HandleAssistantVoiceCommandAsync(
                chatId,
                commandPayload,
                telegram,
                copilotClient,
                sessions,
                assistantTools,
                profile,
                knownFolderExplorerService,
                voiceAdminService,
                textToSpeechService,
                conversationHistories,
                cancellationToken);

            return;
        }

        if (text.StartsWith('/'))
        {
            var command = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0].ToLowerInvariant();
            var commandPayload = string.Empty;
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

                case "/dadjoke":
                    {
                        var searchTerm = ExtractCommandPayload(text);
                        var joke = await dadJokeService.GetJokeAsync(
                            string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm,
                            cancellationToken);
                        await SendReplyWithOptionalTelegramAudioAsync(
                            chatId,
                            EmojiPalette.Wrap(joke, EmojiPalette.Happy, profile.UseEmoji),
                            joke,
                            text,
                            telegram,
                            textToSpeechService,
                            cancellationToken,
                            "Dad joke speak failed");

                        return;
                    }

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

                case "/ticker":
                {
                    var payload = ExtractCommandPayload(text);

                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        await tickerNotificationService.FlushAsync();
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap("Ticker notifications flushed to overlay.", EmojiPalette.Confirm, profile.UseEmoji),
                            cancellationToken);
                    }
                    else
                    {
                        if (payload.Contains(":"))
                        {
                            var parts = payload.Split(':', 2);
                            if (Enum.TryParse<TickerCategory>(parts[0], true, out var parsed))
                            {
                                await tickerNotificationService.EnqueueAndFlushAsync(parts[1].Trim(), parsed);
                                await telegram.SendMessageInChunksAsync(
                                    chatId,
                                    EmojiPalette.Wrap($"Enqueued ticker message with category {parsed} and flushed.", EmojiPalette.Confirm, profile.UseEmoji),
                                    cancellationToken);
                                return;
                            }
                        }

                        await tickerNotificationService.EnqueueAndFlushAsync(payload, TickerCategory.Info);
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap("Enqueued ticker message and flushed.", EmojiPalette.Confirm, profile.UseEmoji),
                            cancellationToken);
                    }

                    return;
                }

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
                    var removed = sessions.TryRemove(chatId, out var removedSession);
                    if (removed)
                    {
                        await removedSession.DisposeAsync();
                    }

                    Console.WriteLine($"[copilot.session.reset] chat={chatId} removed={removed}");

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

                case "/model":
                    {
                        var configuredModel = SystemPromptBuilder.GetConfiguredModel();
                        try
                        {
                            var session = await GetOrCreateSessionAsync(
                                chatId,
                                telegram,
                                knownFolderExplorerService,
                                copilotClient,
                                sessions,
                                assistantTools,
                                profile,
                                conversationHistories);

                            await ModelSelectionGuard.EnsureSessionUsesConfiguredModelAsync(
                                session,
                                configuredModel,
                                context: $"telegram-model-command-{chatId}",
                                cancellationToken);

                            var active = await session.Rpc.Model.GetCurrentAsync(cancellationToken);
                            var activeModel = string.IsNullOrWhiteSpace(active?.ModelId) ? "(unknown)" : active.ModelId;

                            await telegram.SendMessageInChunksAsync(
                                chatId,
                                $"Configured model: {configuredModel}\nActive model: {activeModel}",
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            await telegram.SendMessageInChunksAsync(
                                chatId,
                                EmojiPalette.Wrap($"Model lock failed: {ex.Message}", EmojiPalette.Warning, profile.UseEmoji),
                                cancellationToken);
                        }

                        return;
                    }

                case "/natural":
                    commandPayload = ExtractCommandPayload(text);

                    if (string.IsNullOrWhiteSpace(commandPayload))
                    {
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap("Usage: /natural <command>", EmojiPalette.Warning, profile.UseEmoji),
                            cancellationToken);
                        return;
                    }

                    var naturalResult = await naturalCommandsService.ExecuteAsync(commandPayload, cancellationToken);
                    var naturalContent = EmojiPalette.Wrap(naturalResult.Message, EmojiPalette.Rocket, profile.UseEmoji);
                    await SendReplyWithOptionalTelegramAudioAsync(
                        chatId,
                        naturalContent,
                        naturalResult.Message,
                        text,
                        telegram,
                        textToSpeechService,
                        cancellationToken,
                        "/natural audio failed");
                    return;

                case "/nc":
                    commandPayload = ExtractCommandPayload(text);

                    if (string.IsNullOrWhiteSpace(commandPayload))
                    {
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap("Usage: /nc <your question or command>", EmojiPalette.Warning, profile.UseEmoji),
                            cancellationToken);
                        return;
                    }

                    await HandleAssistantVoiceCommandAsync(
                        chatId,
                        commandPayload,
                        telegram,
                        copilotClient,
                        sessions,
                        assistantTools,
                        profile,
                        knownFolderExplorerService,
                        voiceAdminService,
                        textToSpeechService,
                        conversationHistories,
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
                        webBrowserService,
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

                case "/pron-add":
                    commandPayload = ExtractCommandPayload(text);
                    if (!TryParsePronunciationPayload(commandPayload, out var addWord, out var addReplacement, out var addIpa))
                    {
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap("Usage: /pron-add <word> as <replacement> [ipa <ipa>]  (or /pron-add <word>=<replacement> [ipa <ipa>])", EmojiPalette.Warning, profile.UseEmoji),
                            cancellationToken);
                        return;
                    }

                    var addSsmlPhoneme = BuildSsmlPhoneme(addWord, addIpa);
                    var addContext = addIpa is null
                        ? "Added from Telegram /pron-add command."
                        : $"Added from Telegram /pron-add command. IPA: {addIpa}";

                    await pronunciationService.AddCorrectionAsync(
                        addWord,
                        addReplacement,
                        addSsmlPhoneme,
                        addContext,
                        cancellationToken);

                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap($"Saved pronunciation: '{addWord}' -> '{addReplacement}'{(addIpa is null ? string.Empty : $" (IPA {addIpa})") }.", EmojiPalette.Confirm, profile.UseEmoji),
                        cancellationToken);
                    return;

                case "/pron-remove":
                    commandPayload = ExtractCommandPayload(text);
                    if (string.IsNullOrWhiteSpace(commandPayload))
                    {
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap("Usage: /pron-remove <word>", EmojiPalette.Warning, profile.UseEmoji),
                            cancellationToken);
                        return;
                    }

                    await pronunciationService.RemoveCorrectionAsync(commandPayload.Trim(), cancellationToken);
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        EmojiPalette.Wrap($"Removed pronunciation correction for '{commandPayload.Trim()}'.", EmojiPalette.Confirm, profile.UseEmoji),
                        cancellationToken);
                    return;

                case "/pron-list":
                    var pronunciationList = await BuildPronunciationListAsync(pronunciationService, 40);
                    await telegram.SendMessageInChunksAsync(
                        chatId,
                        pronunciationList,
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

    // Handle natural dad joke requests (e.g. "give me a dad joke" or "dad joke about chickens").
    var dadJoke = await TryGetDadJokeAsync(text, dadJokeService, cancellationToken);
    if (!string.IsNullOrWhiteSpace(dadJoke))
    {
        await SendReplyWithOptionalTelegramAudioAsync(
            chatId,
            EmojiPalette.Wrap(dadJoke, EmojiPalette.Happy, profile.UseEmoji),
            dadJoke,
            text,
            telegram,
            textToSpeechService,
            cancellationToken,
            "Dad joke speak failed");

        return;
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

        if (storedAttachment is null && await TryHandleNaturalPodcastPlayAsync(
                chatId,
                text,
                telegram,
                podcastSubscriptionsService,
                webBrowserService,
                profile,
                cancellationToken))
        {
            return;
        }

        // Deterministic path for conversational todo add/list requests to avoid model hallucinations.
        if (storedAttachment is null)
        {
            if (LooksLikeTodoAddRequest(text))
            {
                var todoData = ExtractTodoDataFromText(text);
                if (gitHubTodosService.IsConfigured)
                {
                    var addResult = await gitHubTodosService.AddTodoAsync(todoData.Title, todoData.Description, todoData.Project, cancellationToken);
                    await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap(addResult, EmojiPalette.Confirm, profile.UseEmoji), cancellationToken);
                    return;
                }

                // Voice Admin todos are deprecated; prefer GitHub Personal Todos.
                if (voiceAdminService.IsConfigured && gitHubTodosService.IsConfigured is false)
                {
                    // If only the legacy Voice Admin DB is configured, inform the user it's deprecated and point them to Personal-Todos.
                    await telegram.SendMessageInChunksAsync(chatId,
                        EmojiPalette.Wrap("Voice Admin todos are deprecated. Configure GitHub Personal Todos and use 'add_personal_todo' to create a todo in Personal-Todos.", EmojiPalette.Warning, profile.UseEmoji),
                        cancellationToken);
                    return;
                }

                // If GitHub Personal Todos is not configured, instruct the user how to set it up.
                await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap("No todo backend is configured. Configure GitHub Personal Todos (recommended) by setting GITHUB_PERSONAL_TODOS_TOKEN and GITHUB_TODOS_REPO, then use 'add_personal_todo' to create todos.", EmojiPalette.Warning, profile.UseEmoji), cancellationToken);
                return;
            }

            if (LooksLikeTodoListRequest(text))
            {
                if (gitHubTodosService.IsConfigured)
                {
                    // For GitHub-backed personal todos, list open issues optionally filtered by label.
                    var labelMatch = Regex.Match(text, "\\blabel\\s*(?:=|is|:)?\\s*([\\w\\s-]+)\\b", RegexOptions.IgnoreCase);
                    var label = labelMatch.Success ? labelMatch.Groups[1].Value.Trim() : null;
                    var listResult = await gitHubTodosService.ListOpenIssuesAsync(label, 20, htmlFormat: true, cancellationToken);
                    await telegram.SendMessageInChunksAsync(chatId, listResult, cancellationToken);
                    return;
                }

                if (LooksLikeTodoCsvExportRequest(text))
                {
                    var exportResult = await ExportVoiceAdminTodosToCsvAsync(text, voiceAdminService);
                    await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap(exportResult, EmojiPalette.Confirm, profile.UseEmoji), cancellationToken);
                    return;
                }

                if (voiceAdminService.IsConfigured)
                {
                    var todoTable = await voiceAdminService.ListIncompleteTodosAsync(asHtmlTable: true);
                    await telegram.SendMessageInChunksAsync(chatId, todoTable, cancellationToken);
                    return;
                }

                await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap("No todo backend is configured. Configure GitHub Personal Todos (recommended).", EmojiPalette.Warning, profile.UseEmoji), cancellationToken);
                return;
            }
        }

        if (LooksLikeModelStatusRequest(text))
        {
            await HandleModelStatusRequestAsync(
                chatId,
                text,
                telegram,
                copilotClient,
                sessions,
                assistantTools,
                profile,
                knownFolderExplorerService,
                conversationHistories,
                cancellationToken);
            return;
        }

        var messageOptions = BuildMessageOptions(text, storedAttachment);
        RecordConversationHistory(chatId, "User", text, conversationHistories);

        try
        {
            var content = await SendWithSessionRecoveryAsync(
                chatId,
                messageOptions,
                telegram,
                knownFolderExplorerService,
                copilotClient,
                sessions,
                assistantTools,
                profile,
                conversationHistories,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(content))
            {
                content = "I could not generate a response. Please try again.";
            }

            content = await ReconcileTodoEmptyClaimAsync(content, voiceAdminService);
            content = StripMisleadingModelClaims(content);
            content = NormalizeConfiguredModelSignoff(content);
            RecordConversationHistory(chatId, "Assistant", content, conversationHistories);

            Console.Error.WriteLine($"[tts.info] Main chat response will be spoken or sent as audio (if enabled).");
            await SendReplyWithOptionalTelegramAudioAsync(
                chatId,
                content,
                content,
                text,
                telegram,
                textToSpeechService,
                cancellationToken,
                "Main chat speak failed");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[copilot.session.error] chat={chatId} {ex}");

            // Only remove the cached session when the error indicates session state is invalid.
            if (ShouldRecycleSessionAfterError(ex) && sessions.TryRemove(chatId, out var failedSession))
            {
                await failedSession.DisposeAsync();
            }

            try
            {
                var reason = ClassifyError(ex);
                await telegram.SendMessageInChunksAsync(
                    chatId,
                    EmojiPalette.Wrap($"I hit an error while generating a reply.\nReason: {reason}\nDetail: {ex.Message}", EmojiPalette.Warning, profile.UseEmoji),
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
        TelegramApiClient telegramClient,
        KnownFolderExplorerService folderService,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools,
        PersonalityProfile profile,
        ConcurrentDictionary<long, List<string>> conversationHistories)
    {
        var currentSig = string.Join(";", assistantTools.Select(t => t.Name ?? string.Empty).OrderBy(n => n));

        if (sessions.TryGetValue(chatId, out var existingSession))
        {
            if (SessionToolSignatures.TryGetValue(chatId, out var knownSig) && string.Equals(knownSig, currentSig, StringComparison.Ordinal))
            {
                Console.WriteLine($"[copilot.session.reuse] chat={chatId}");
                return existingSession;
            }

            Console.WriteLine($"[copilot.session.tools_changed] chat={chatId} disposing stale session due to toolset change");
            if (sessions.TryRemove(chatId, out var removed))
            {
                await removed.DisposeAsync();
            }

            SessionToolSignatures.TryRemove(chatId, out _);
        }

        var sendFileTool = AIFunctionFactory.Create(
            async (
                [Description("Folder alias: documents, desktop, downloads, pictures, videos, repo, repos")] string folderAlias,
                [Description("Relative file path inside the selected folder root")] string relativeFilePath) =>
            {
                try
                {
                    if (!folderService.TryResolveFilePath(folderAlias, relativeFilePath, out var resolvedPath))
                    {
                        return $"File not found or path not allowed: {relativeFilePath} (alias {folderAlias}).";
                    }

                    await telegramClient.SendDocumentAsync(chatId, resolvedPath, CancellationToken.None);
                    return $"Sent file '{relativeFilePath}' from '{folderAlias}' to Telegram chat.";
                }
                catch (Exception ex)
                {
                    return $"Failed to send file: {ex.Message}";
                }
            },
            "send_file_to_telegram",
            "Send a file from a known folder to the current Telegram chat. Provide folder alias and relative file path.");

        var sessionTools = assistantTools.Append(sendFileTool).ToList();

        var systemPrompt = SystemPromptBuilder.Build(profile);
        if (conversationHistories.TryGetValue(chatId, out var previousHistory) && previousHistory.Count > 0)
        {
            var snippet = string.Join("\n", previousHistory.TakeLast(12));
            systemPrompt += "\n\n" +
                "[NOTE: previous conversation context preserved for this chat]\n" +
                snippet;
        }

        var configuredModel = SystemPromptBuilder.GetConfiguredModel();
        var createdSession = await copilotClient.CreateSessionAsync(new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Model = configuredModel,
            Tools = sessionTools,
            SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt
            }
        });

        try
        {
            await ModelSelectionGuard.EnsureSessionUsesConfiguredModelAsync(
                createdSession,
                configuredModel,
                context: $"telegram-chat-{chatId}",
                CancellationToken.None);
        }
        catch
        {
            await createdSession.DisposeAsync();
            throw;
        }

        if (sessions.TryAdd(chatId, createdSession))
        {
            SessionToolSignatures[chatId] = currentSig;
            Console.WriteLine($"[copilot.session.create] chat={chatId}");
            return createdSession;
        }

        Console.WriteLine($"[copilot.session.race] chat={chatId} another session already exists; disposing newly created duplicate");
        await createdSession.DisposeAsync();
        return sessions[chatId];
    }

    private static string NormalizeConfiguredModelSignoff(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var configuredModel = SystemPromptBuilder.GetConfiguredModel();
        return Regex.Replace(
            content,
            "(?im)^\\s*out(?:\\s+.+)?\\s*$",
            $"Out {configuredModel}");
    }

    private static async Task<string?> SendWithSessionRecoveryAsync(
        long chatId,
        MessageOptions messageOptions,
        TelegramApiClient telegram,
        KnownFolderExplorerService knownFolderExplorerService,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools,
        PersonalityProfile profile,
        ConcurrentDictionary<long, List<string>> conversationHistories,
        CancellationToken cancellationToken)
    {
        var session = await GetOrCreateSessionAsync(chatId, telegram, knownFolderExplorerService, copilotClient, sessions, assistantTools, profile, conversationHistories);

        try
        {
            var assistantReply = await session.SendAndWaitAsync(messageOptions, null, cancellationToken);
            return assistantReply?.Data.Content?.Trim();
        }
        catch (Exception ex) when (IsSendTimeoutError(ex))
        {
            Console.Error.WriteLine($"[copilot.session.retry] chat={chatId} SendAndWaitAsync timed out; retrying once with a fresh session.");

            await telegram.SendMessageInChunksAsync(
                chatId,
                EmojiPalette.Wrap("I'm busy right now — retrying in a moment...", EmojiPalette.Warning, profile.UseEmoji),
                cancellationToken);

            if (sessions.TryRemove(chatId, out var timedOutSession))
            {
                await timedOutSession.DisposeAsync();
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

            var recreatedSession = await GetOrCreateSessionAsync(chatId, telegram, knownFolderExplorerService, copilotClient, sessions, assistantTools, profile, conversationHistories);
            var assistantReply = await recreatedSession.SendAndWaitAsync(messageOptions, null, cancellationToken);
            return assistantReply?.Data.Content?.Trim();
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            Console.Error.WriteLine($"[copilot.session.recover] chat={chatId} Session not found; recreating session and retrying once.");

            if (sessions.TryRemove(chatId, out var staleSession))
            {
                await staleSession.DisposeAsync();
            }

            var recreatedSession = await GetOrCreateSessionAsync(chatId, telegram, knownFolderExplorerService, copilotClient, sessions, assistantTools, profile, conversationHistories);
            var assistantReply = await recreatedSession.SendAndWaitAsync(messageOptions, null, cancellationToken);
            return assistantReply?.Data.Content?.Trim();
        }
    }

    private static void RecordConversationHistory(long chatId, string role, string text, ConcurrentDictionary<long, List<string>> conversationHistories)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var normalizedText = text.Trim().Replace("\r\n", " ").Replace("\n", " ");
        var entry = $"{role}: {normalizedText}";

        var history = conversationHistories.GetOrAdd(chatId, _ => new List<string>());

        lock (history)
        {
            history.Add(entry);

            if (history.Count > 120)
            {
                history.RemoveRange(0, history.Count - 120);
            }
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

    private static bool IsSendTimeoutError(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)
                && current.Message.Contains("SendAndWaitAsync timed out", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private static bool ShouldRecycleSessionAfterError(Exception exception)
    {
        return IsSessionNotFoundError(exception) || IsSendTimeoutError(exception);
    }

    private static string ClassifyError(Exception exception)
    {
        if (IsSendTimeoutError(exception))
            return "The AI model took too long to respond (>1 min). This usually means Copilot is under heavy load. Try again in a moment.";

        if (IsSessionNotFoundError(exception))
            return "The AI session expired or was lost. A fresh session has been created — try sending your message again.";

        if (exception is OperationCanceledException or TaskCanceledException)
            return "The request was cancelled, likely because it was taking too long. Try again.";

        if (exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("429", StringComparison.Ordinal))
            return "Rate limit hit — too many requests in a short period. Wait a few seconds and try again.";

        if (exception.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("401", StringComparison.Ordinal))
            return "Authentication failed. The Copilot token may have expired — a restart may be needed.";

        if (exception.Message.Contains("network", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("connection", StringComparison.OrdinalIgnoreCase)
            || exception is System.Net.Http.HttpRequestException)
            return "Network or connection error communicating with Copilot. Check internet connectivity.";

        return $"Unexpected error ({exception.GetType().Name}). Check the server logs for the full stack trace.";
    }

    private static async Task HandleAssistantVoiceCommandAsync(
        long chatId,
        string commandPayload,
        TelegramApiClient telegram,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools,
        PersonalityProfile profile,
        KnownFolderExplorerService knownFolderExplorerService,
        VoiceAdminService voiceAdminService,
        TextToSpeechService textToSpeechService,
        ConcurrentDictionary<long, List<string>> conversationHistories,
        CancellationToken cancellationToken)
    {
        var messageOptions = new MessageOptions { Prompt = commandPayload };

        RecordConversationHistory(chatId, "User", commandPayload, conversationHistories);

        var content = await SendWithSessionRecoveryAsync(
            chatId,
            messageOptions,
            telegram,
            knownFolderExplorerService,
            copilotClient,
            sessions,
            assistantTools,
            profile,
            conversationHistories,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            content = "I could not generate a response. Please try again.";
        }

        content = await ReconcileTodoEmptyClaimAsync(content, voiceAdminService);
        content = StripMisleadingModelClaims(content);
        content = NormalizeConfiguredModelSignoff(content);
        RecordConversationHistory(chatId, "Assistant", content, conversationHistories);

        await SendReplyWithOptionalTelegramAudioAsync(
            chatId,
            content,
            content,
            commandPayload,
            telegram,
            textToSpeechService,
            cancellationToken,
            $"[tts.speak.error] chat={chatId}");
    }

    private static bool LooksLikeTodoListRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var mentionsTodo = Regex.IsMatch(text, "\\b(todos?|to\\s*do|task|tasks|pending|open items?)\\b", RegexOptions.IgnoreCase);

        // Avoid matching non-list sentences in conversational content, e.g. "what they are" or "add a todo".
        var mentionsAdd = Regex.IsMatch(text, "\\b(add|create|remind|note|new)\\b", RegexOptions.IgnoreCase);
        if (mentionsAdd && mentionsTodo)
            return false;

        var asksToList = Regex.IsMatch(text,
            "\\b(" +
            "todo list|list (todos?|tasks?)|show (todos?|tasks?)|check (todos?|tasks?)|" +
            "open (todos?|tasks?)|what.*(left|pending|open|todos?|tasks?)|still (todos?|tasks?)" +
            ")\\b",
            RegexOptions.IgnoreCase);

        return mentionsTodo && asksToList;
    }

    private static bool LooksLikeModelStatusRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Regex.IsMatch(
            text,
            "\\b(current|actual|active|using|used|running)\\b.{0,40}\\b(model|model id)\\b|\\bwhat model\\b|\\b/model\\b",
            RegexOptions.IgnoreCase);
    }

    private static async Task HandleModelStatusRequestAsync(
        long chatId,
        string userText,
        TelegramApiClient telegram,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools,
        PersonalityProfile profile,
        KnownFolderExplorerService knownFolderExplorerService,
        ConcurrentDictionary<long, List<string>> conversationHistories,
        CancellationToken cancellationToken)
    {
        var configuredModel = SystemPromptBuilder.GetConfiguredModel();

        try
        {
            var session = await GetOrCreateSessionAsync(
                chatId,
                telegram,
                knownFolderExplorerService,
                copilotClient,
                sessions,
                assistantTools,
                profile,
                conversationHistories);

            await ModelSelectionGuard.EnsureSessionUsesConfiguredModelAsync(
                session,
                configuredModel,
                context: $"telegram-model-status-{chatId}",
                cancellationToken);

            var active = await session.Rpc.Model.GetCurrentAsync(cancellationToken);
            var activeModel = string.IsNullOrWhiteSpace(active?.ModelId) ? "(unknown)" : active.ModelId;

            RecordConversationHistory(chatId, "User", userText, conversationHistories);
            var statusReply = $"Configured model: {configuredModel}\nActive model: {activeModel}\nOut {configuredModel}";
            RecordConversationHistory(chatId, "Assistant", statusReply, conversationHistories);

            await telegram.SendMessageInChunksAsync(chatId, statusReply, cancellationToken);
        }
        catch (Exception ex)
        {
            await telegram.SendMessageInChunksAsync(
                chatId,
                EmojiPalette.Wrap($"Model lock/status check failed: {ex.Message}", EmojiPalette.Warning, profile.UseEmoji),
                cancellationToken);
        }
    }

    private static string StripMisleadingModelClaims(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var sanitized = Regex.Replace(
            content,
            "(?im)^.*\\b(powered by|underlying model|model id)\\b.*(?:\\r?\\n)?",
            string.Empty);

        return sanitized.Trim();
    }

    private static bool LooksLikeNoTodoClaim(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return Regex.IsMatch(content, "\\b(no|none|empty)\\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, "\\b(todos?|to\\s*do|task|tasks)\\b", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeTodoAddRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var mentionsTodo = Regex.IsMatch(text, "\\b(todos?|to\\s*do|task|tasks?)\\b", RegexOptions.IgnoreCase);
        var mentionsAdd = Regex.IsMatch(text, "\\b(add|create|remind|note|new)\\b", RegexOptions.IgnoreCase);
        return mentionsTodo && mentionsAdd;
    }

    private static TodoData ExtractTodoDataFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TodoData("New todo", null, null, 0);

        string? title = null;
        string? description = null;
        string? project = null;
        int priority = 0;

        // Look for structured format: "title of X and description of Y"
        var lowerText = text.ToLowerInvariant();

        // Extract title: "title of [X] and/description/priority"
        var titleOfIndex = lowerText.IndexOf("title of ", StringComparison.OrdinalIgnoreCase);
        if (titleOfIndex >= 0)
        {
            titleOfIndex += "title of ".Length;
            // Find where title ends: look for " and ", " description ", " priority ", or end
            int titleEndIndex = FindNextDelimiter(lowerText, titleOfIndex, new[] { " and ", " description of ", " priority ", " project " });
            if (titleEndIndex > titleOfIndex)
            {
                title = text.Substring(titleOfIndex, titleEndIndex - titleOfIndex).Trim();
            }
        }

        // Extract description: "description of [X] and/priority/project"
        var descriptionOfIndex = lowerText.IndexOf("description of ", StringComparison.OrdinalIgnoreCase);
        if (descriptionOfIndex >= 0)
        {
            descriptionOfIndex += "description of ".Length;
            // Find where description ends: look for " and ", " priority ", " project ", " make it a priority"
            int descEndIndex = FindNextDelimiter(lowerText, descriptionOfIndex, new[] { " and ", " priority ", " project ", " make it ", " make a priority " });
            if (descEndIndex > descriptionOfIndex)
            {
                description = text.Substring(descriptionOfIndex, descEndIndex - descriptionOfIndex).Trim();
            }
        }

        // Extract priority: look for "priority of X" or "make it a priority of X"
        var priorityMatch = Regex.Match(text, @"(?:priority\s+of|make\s+(?:it\s+)?a?\s+priority\s+of)\s+([0-9]+|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)", RegexOptions.IgnoreCase);
        if (priorityMatch.Success)
        {
            var priorityStr = priorityMatch.Groups[1].Value;
            if (int.TryParse(priorityStr, out int p))
            {
                priority = p;
            }
            else
            {
                priority = TextNumberToInt(priorityStr);
            }
        }

        // Extract project: "project of X" or "project is X"
        var projectMatch = Regex.Match(text, @"\bproject\s+(?:of|is)\s+([^\s]+(?:\s+[^\s]+){0,3})(?:\s+and|\s+priority|\s+description|\s|$)", RegexOptions.IgnoreCase);
        if (projectMatch.Success && !string.IsNullOrWhiteSpace(projectMatch.Groups[1].Value))
        {
            project = projectMatch.Groups[1].Value.Trim();
        }

        // If no explicit title was found, try the old extraction method
        if (string.IsNullOrWhiteSpace(title))
        {
            title = ExtractTodoTitleFromText(text);
        }

        // Heuristic: if we ended up using the full message as the title (or the title is very long),
        // prefer to store the full message as the description and generate a concise title instead.
        var sanitizedFull = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(description))
        {
            var usedFullAsTitle = string.Equals(title?.Trim(), sanitizedFull, StringComparison.Ordinal);
            if (usedFullAsTitle || (title?.Length ?? 0) > 120)
            {
                description = sanitizedFull;

                // Local helper to produce a short, readable title from a longer sentence.
                static string ShortTitleFrom(string s, int maxLen = 60)
                {
                    if (string.IsNullOrWhiteSpace(s)) return "New todo";
                    s = s.Trim();
                    // Try first sentence
                    var firstPunct = s.IndexOfAny(new[] { '.', '!', '?' });
                    if (firstPunct > 0)
                        s = s.Substring(0, firstPunct).Trim();

                    // If still long, prefer text before common clause words
                    var clauseDelims = new[] { " to ", " for ", " so that ", " because ", "," };
                    foreach (var d in clauseDelims)
                    {
                        var idx = s.IndexOf(d, StringComparison.OrdinalIgnoreCase);
                        if (idx > 0)
                        {
                            s = s.Substring(0, idx).Trim();
                            break;
                        }
                    }

                    if (s.Length > maxLen)
                        s = s.Substring(0, maxLen).Trim();

                    // Remove trailing non-word punctuation
                    s = Regex.Replace(s, @"[\u2000-\u206F\p{P}\p{S}]+$", string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(s)) return "New todo";
                    return s;
                }

                title = ShortTitleFrom(sanitizedFull, 60);
            }
        }

        // Ensure title length is reasonable (keep titles short for GitHub Issues list readability)
        if (!string.IsNullOrWhiteSpace(title) && title.Length > 60)
            title = title.Substring(0, 60).Trim() + "...";

        // Ensure description length is reasonable
        if (!string.IsNullOrWhiteSpace(description) && description.Length > 500)
            description = description.Substring(0, 500).Trim() + "...";

        return new TodoData(title ?? "New todo", description, project, priority);
    }

    private static int FindNextDelimiter(string text, int startIndex, string[] delimiters)
    {
        int closestIndex = text.Length;
        foreach (var delimiter in delimiters)
        {
            var index = text.IndexOf(delimiter, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && index < closestIndex)
            {
                closestIndex = index;
            }
        }
        return closestIndex;
    }

    private static int TextNumberToInt(string text)
    {
        var lowerText = text.Trim().ToLowerInvariant();
        return lowerText switch
        {
            "zero" => 0,
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "eleven" => 11,
            "twelve" => 12,
            "thirteen" => 13,
            "fourteen" => 14,
            "fifteen" => 15,
            "sixteen" => 16,
            "seventeen" => 17,
            "eighteen" => 18,
            "nineteen" => 19,
            "twenty" => 20,
            "thirty" => 30,
            "forty" => 40,
            "fifty" => 50,
            "sixty" => 60,
            "seventy" => 70,
            "eighty" => 80,
            "ninety" => 90,
            _ => 0
        };
    }

    private class TodoData
    {
        public string Title { get; }
        public string? Description { get; }
        public string? Project { get; }
        public int Priority { get; }

        public TodoData(string title, string? description, string? project, int priority)
        {
            Title = title;
            Description = description;
            Project = project;
            Priority = priority;
        }
    }

    private static string ExtractTodoTitleFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "New todo";

        var match = Regex.Match(text,
            "\\b(?:remind me to|add (?:a )?new (?:todo|task)|add (?:todo|task)|create (?:todo|task))\\s+(.+?)($|\\.|\\?|!|\\bas that\\b|\\bso that\\b|\\bbecause\\b|\\bplease\\b)",
            RegexOptions.IgnoreCase);

        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            var title = match.Groups[1].Value.Trim();
            if (title.Length > 180)
                title = title.Substring(0, 180).Trim() + "...";
            return title;
        }

        var sanitized = text.Trim();
        if (sanitized.Length > 180)
            sanitized = sanitized.Substring(0, 180).Trim() + "...";

        return sanitized;
    }

    private static bool LooksLikeTodoCsvExportRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var exportWords = "\\b(csv|comma[- ]?separated|spreadsheet|excel|export|download|save to file)\\b";
        return LooksLikeTodoListRequest(text) && Regex.IsMatch(text, exportWords, RegexOptions.IgnoreCase);
    }

    private static async Task<string> ExportVoiceAdminTodosToCsvAsync(string text, VoiceAdminService voiceAdminService)
    {
        var projectOrCategoryMatch = Regex.Match(text, "\\b(project|category)\\s*(?:=|is|:)?\\s*([\\w\\s-]+)\\b", RegexOptions.IgnoreCase);
        var projectOrCategory = projectOrCategoryMatch.Success ? projectOrCategoryMatch.Groups[2].Value.Trim() : null;

        var maxResultMatch = Regex.Match(text, "\\b(top|first|latest)\\s+(\\d{1,3})\\b", RegexOptions.IgnoreCase);
        var maxResults = maxResultMatch.Success ? int.Parse(maxResultMatch.Groups[2].Value) : (int?)null;

        var rowsResult = await voiceAdminService.GetIncompleteTodosRowsAsync(projectOrCategory, maxResults);
        if (!rowsResult.Success)
            return rowsResult.Message;

        if (!rowsResult.Rows.Any())
            return "No incomplete Voice Admin todo items found.";

        var repoRoot = Path.GetFullPath(EnvironmentSettings.ReadString("ASSISTANT_REPO_DIRECTORY", Directory.GetCurrentDirectory()));
        var exportFolder = Path.Combine(repoRoot, "db_exports");
        Directory.CreateDirectory(exportFolder);

        var outputFileName = SanitizeFileName($"voice_admin_todos_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        var fullPath = Path.Combine(exportFolder, outputFileName);

        try
        {
            File.WriteAllText(fullPath, BuildCsvContent(rowsResult.Rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            return $"Failed to write CSV file '{fullPath}': {ex.Message}";
        }

        try
        {
            var codeProcess = new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"\"{fullPath}\"",
                UseShellExecute = true,
                WorkingDirectory = exportFolder
            };

            Process.Start(codeProcess);
            return $"Voice Admin CSV exported to {fullPath}. Opened in Visual Studio Code.";
        }
        catch (Exception ex)
        {
            return $"Voice Admin CSV exported to {fullPath}. Could not open in VS Code automatically: {ex.Message}";
        }
    }

    private static string BuildCsvContent(IEnumerable<IDictionary<string, object?>> rows)
    {
        var first = rows.FirstOrDefault();
        if (first == null)
            return string.Empty;

        var columns = first.Keys.ToArray();
        var sb = new StringBuilder();

        sb.AppendLine(string.Join(",", columns.Select(EscapeCsvValue)));

        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", columns.Select(col => EscapeCsvValue(row.ContainsKey(col) ? row[col] : null))));
        }

        return sb.ToString();
    }

    private static string EscapeCsvValue(object? value)
    {
        if (value == null)
            return string.Empty;

        var asString = value.ToString() ?? string.Empty;
        var needsQuotes = asString.Contains(',') || asString.Contains('"') || asString.Contains('\n') || asString.Contains('\r');
        var escaped = asString.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(c.ToString(), "_");
        }

        return fileName;
    }

    private static async Task<string> ReconcileTodoEmptyClaimAsync(string content, VoiceAdminService voiceAdminService)
    {
        if (!voiceAdminService.IsConfigured || !LooksLikeNoTodoClaim(content))
            return content;

        var verified = await voiceAdminService.ListIncompleteTodosAsync(asHtmlTable: true);
        var isActuallyEmpty = verified.StartsWith("No incomplete Voice Admin todo items found.", StringComparison.OrdinalIgnoreCase)
            || verified.StartsWith("No incomplete Voice Admin todo items found for project/category matching", StringComparison.OrdinalIgnoreCase);

        return isActuallyEmpty ? content : verified;
    }

    private static bool IsAudioReplyRequested(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(text,
            @"\b(reply|respond|send|give|answer)\b.{0,20}\b(?:as|in|with)\b.{0,10}\b(audio|voice|speech|wav|wave(?:\s+file)?|spoken)\b" +
            @"|\b(reply|respond|send|give|answer)\b.{0,20}\b(audio|voice|speech|wav|wave(?:\s+file)?|spoken)\b.{0,10}\b(reply|response|message)\b" +
            @"|\b(audio|voice|speech|wav|wave(?:\s+file)?|spoken)\b.{0,20}\b(reply|response|message)\b" +
            @"|\b(read|say)\b.{0,20}\b(out\s*loud|as\s+(audio|voice|speech|wav|wave(?:\s+file)?))\b",
            RegexOptions.IgnoreCase);
    }

    private static bool IsTextRepresentationOfAudioRequested(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var mentionsAudioSource = Regex.IsMatch(text,
            @"\b(audio|voice|speech|wav|wave|recording|recorded|voice\s+note|sound\s+file)\b",
            RegexOptions.IgnoreCase);

        if (!mentionsAudioSource)
            return false;

        return Regex.IsMatch(text,
            @"\b(transcrib(?:e|ed|ing)|transcript|text\s+representation|text\s+version|written\s+version|write\s+(?:it|that|this)?\s*out|as\s+text|in\s+text|into\s+text|to\s+text)\b",
            RegexOptions.IgnoreCase);
    }

    private static bool ShouldSendTelegramAudio(string? userRequestText)
    {
        if (IsTextRepresentationOfAudioRequested(userRequestText ?? string.Empty))
        {
            return false;
        }

        var userWantsAudio = userRequestText is not null && IsAudioReplyRequested(userRequestText);
        // Only send synthesized WAV files when the user explicitly requests audio.
        // Previously this also sent audio when Telegram wasn't focused; that behavior is disabled.
        return userWantsAudio;
    }

    private static async Task SendReplyWithOptionalTelegramAudioAsync(
        long chatId,
        string telegramText,
        string speechText,
        string? userRequestText,
        TelegramApiClient telegram,
        TextToSpeechService textToSpeechService,
        CancellationToken cancellationToken,
        string ttsErrorContext)
    {
        if (IsTextRepresentationOfAudioRequested(userRequestText ?? string.Empty))
        {
            await telegram.SendMessageInChunksAsync(chatId, telegramText, cancellationToken);
            return;
        }

        var sendTelegramAudio = ShouldSendTelegramAudio(userRequestText);

        if (!sendTelegramAudio)
        {
            await telegram.SendMessageInChunksAsync(chatId, telegramText, cancellationToken);
        }

        try
        {
            await SpeakOrSendAudioAsync(chatId, speechText, textToSpeechService, telegram, cancellationToken, userRequestText);
        }
        catch (Exception ttsEx)
        {
            Console.Error.WriteLine($"[tts.error] {ttsErrorContext}: {ttsEx.Message}");

            // Audio-only mode should still deliver a response if synthesis/upload fails.
            if (sendTelegramAudio)
            {
                await telegram.SendMessageInChunksAsync(chatId, telegramText, cancellationToken);
            }
        }
    }

    private static bool IsTelegramDesktopFocused()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
                return false;

            NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var foregroundPid);
            if (foregroundPid == 0)
                return false;

            var telegramProcesses = Process.GetProcessesByName("Telegram");
            return telegramProcesses.Any(p => (uint)p.Id == foregroundPid);
        }
        catch
        {
            return false;
        }
    }

    // Speaks the text locally when possible (user at the PC).
    // By default, do not send synthesized WAV files; only send audio when the user
    // explicitly requests an audio reply in their message.
    private static async Task SpeakOrSendAudioAsync(
        long chatId,
        string text,
        TextToSpeechService textToSpeechService,
        TelegramApiClient telegram,
        CancellationToken cancellationToken,
        string? userRequestText = null)
    {
        if (!ShouldSendTelegramAudio(userRequestText))
        {
            await textToSpeechService.TrySpeakPreviewAsync(text, cancellationToken);
        }
        else
        {
            // Force synthesis: user explicitly requested audio.
            var tmpFile = await textToSpeechService.SynthesizePreviewToWavFileAsync(text, cancellationToken, force: true);
            if (!string.IsNullOrWhiteSpace(tmpFile))
            {
                try
                {
                    await telegram.SendDocumentAsync(chatId, tmpFile, cancellationToken);
                }
                finally
                {
                    try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
                }
            }
        }
    }

    private static string ExtractCommandPayload(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        return parts.Length < 2 ? string.Empty : parts[1];
    }

    private static async Task<string?> TryGetDadJokeAsync(string text, DadJokeService dadJokeService, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Only trigger on explicit dad joke requests.
        if (!Regex.IsMatch(text, "\\bdad\\s*joke\\b", RegexOptions.IgnoreCase))
        {
            return null;
        }

        // Attempt to find an optional search term (e.g. "dad joke about chickens").
        var match = Regex.Match(text, "\\bdad\\s*joke(?:\\s*(?:about|on|of)\\s+(.+))?$", RegexOptions.IgnoreCase);
        var term = match.Success && match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : string.Empty;

        // Ensure we don't pass empty term to the API.
        return await dadJokeService.GetJokeAsync(string.IsNullOrWhiteSpace(term) ? null : term, cancellationToken);
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
            FormatCommandLine("/pron-add <word> as <replacement> [ipa <ipa>]", "add pronunciation correction for TTS", EmojiPalette.Confirm, profile.UseEmoji),
            FormatCommandLine("/pron-remove <word>", "remove pronunciation correction", EmojiPalette.Confirm, profile.UseEmoji),
            FormatCommandLine("/pron-list", "list pronunciation corrections", EmojiPalette.Commands, profile.UseEmoji),
            FormatCommandLine("/weather", "open BBC Weather for Maidstone, Kent", EmojiPalette.Search, profile.UseEmoji),
            FormatCommandLine("/dadjoke [keyword]", "get a random dad joke (optional keyword)", EmojiPalette.Happy, profile.UseEmoji),
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

    private static bool TryParsePronunciationPayload(string payload, out string originalWord, out string replacementWord, out string? ipa)
    {
        originalWord = string.Empty;
        replacementWord = string.Empty;
        ipa = null;

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var normalized = payload.Trim();

        var ipaMatch = Regex.Match(
            normalized,
            "\\s+(?:ipa|phoneme)\\s*[=:]?\\s*(?<ipa>.+)$",
            RegexOptions.IgnoreCase);

        if (ipaMatch.Success)
        {
            ipa = NormalizeIpaToken(ipaMatch.Groups["ipa"].Value);
            normalized = normalized[..ipaMatch.Index].Trim();
            if (string.IsNullOrWhiteSpace(ipa))
            {
                ipa = null;
            }
        }

        var equalsParts = normalized.Split('=', 2, StringSplitOptions.TrimEntries);
        if (equalsParts.Length == 2)
        {
            originalWord = NormalizePronunciationToken(equalsParts[0]);
            replacementWord = NormalizePronunciationToken(equalsParts[1]);
            return !string.IsNullOrWhiteSpace(originalWord) && !string.IsNullOrWhiteSpace(replacementWord);
        }

        var asMatch = Regex.Match(
            normalized,
            "^(?<word>.+?)\\s+(?:as|to|like)\\s+(?<replacement>.+)$",
            RegexOptions.IgnoreCase);

        if (!asMatch.Success)
            return false;

        originalWord = NormalizePronunciationToken(asMatch.Groups["word"].Value);
        replacementWord = NormalizePronunciationToken(asMatch.Groups["replacement"].Value);
        return !string.IsNullOrWhiteSpace(originalWord) && !string.IsNullOrWhiteSpace(replacementWord);
    }

    private static bool TryParsePronunciationAddRequest(string text, out string originalWord, out string replacementWord, out string? ipa)
    {
        originalWord = string.Empty;
        replacementWord = string.Empty;
        ipa = null;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();

        var directAddMatch = Regex.Match(
            normalized,
            "^(?:please\\s+)?(?:add|set|save|create)\\s+(?:a\\s+)?pronunciation(?:\\s+for)?\\s+(?<payload>.+)$",
            RegexOptions.IgnoreCase);

        if (directAddMatch.Success)
        {
            return TryParsePronunciationPayload(directAddMatch.Groups["payload"].Value, out originalWord, out replacementWord, out ipa);
        }

        var pronounceMatch = Regex.Match(
            normalized,
            "^(?:please\\s+)?(?:pronounce|say)\\s+(?<payload>.+)$",
            RegexOptions.IgnoreCase);

        if (pronounceMatch.Success)
        {
            return TryParsePronunciationPayload(pronounceMatch.Groups["payload"].Value, out originalWord, out replacementWord, out ipa);
        }

        return false;
    }

    private static string? BuildSsmlPhoneme(string originalWord, string? ipa)
    {
        if (string.IsNullOrWhiteSpace(originalWord) || string.IsNullOrWhiteSpace(ipa))
            return null;

        var safeWord = EscapeXml(originalWord.Trim());
        var safeIpa = EscapeXmlAttribute(ipa.Trim());
        return $"<phoneme alphabet='ipa' ph='{safeIpa}'>{safeWord}</phoneme>";
    }

    private static string NormalizeIpaToken(string value)
    {
        var token = value
            .Trim()
            .Trim('"')
            .Trim('\'')
            .Trim();

        if (token.Length >= 2 && token.StartsWith('/') && token.EndsWith('/'))
        {
            token = token[1..^1].Trim();
        }

        return token;
    }

    private static string EscapeXml(string input)
    {
        return input
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static string EscapeXmlAttribute(string input)
    {
        return EscapeXml(input);
    }

    private static string? TryExtractIpaFromSsml(string? ssmlPhoneme)
    {
        if (string.IsNullOrWhiteSpace(ssmlPhoneme))
            return null;

        var match = Regex.Match(ssmlPhoneme, "ph=['\"](?<ipa>[^'\"]+)['\"]", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        return match.Groups["ipa"].Value.Trim();
    }

    private static string NormalizePronunciationToken(string value)
    {
        return value
            .Trim()
            .Trim('"')
            .Trim('\'')
            .Trim();
    }

    private static async Task<string> BuildPronunciationListAsync(PronunciationDictionaryService pronunciationService, int maxItems)
    {
        var lines = new List<string>();
        await foreach (var entry in pronunciationService.ListAllAsync())
        {
            var ipa = TryExtractIpaFromSsml(entry.SsmlPhoneme);
            var ipaSuffix = string.IsNullOrWhiteSpace(ipa) ? string.Empty : $" (IPA {ipa})";
            lines.Add($"- {entry.OriginalWord} -> {entry.Replacement}{ipaSuffix}");
            if (lines.Count >= maxItems)
            {
                break;
            }
        }

        if (lines.Count == 0)
        {
            return "No pronunciation corrections are configured.";
        }

        return "Pronunciation corrections:\n" + string.Join("\n", lines);
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
        WebBrowserAssistantService webBrowserService,
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

        var playbackResult = await PlayPodcastEpisodeAsync(
            podcastName,
            episodeNumber,
            podcastSubscriptionsService,
            webBrowserService);

        await telegram.SendMessageInChunksAsync(
            chatId,
            EmojiPalette.Wrap(playbackResult, EmojiPalette.Music, profile.UseEmoji),
            cancellationToken);
    }

    private static async Task HandleAddPodcastCommandAsync(
        long chatId,
        string text,
        TelegramApiClient telegram,
        PodcastSubscriptionsService podcastSubscriptionsService,
        PersonalityProfile profile,
        CancellationToken cancellationToken)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 3)
        {
            await telegram.SendMessageInChunksAsync(
                chatId,
                EmojiPalette.Wrap("Usage: /add-podcast <podcast-name> <search-term>", EmojiPalette.Music, profile.UseEmoji),
                cancellationToken);
            return;
        }

        var podcastName = parts[1];
        var searchTerm = string.Join(' ', parts.Skip(2));

        await podcastSubscriptionsService.AddSubscriptionAsync(podcastName, searchTerm);

        await telegram.SendMessageInChunksAsync(
            chatId,
            EmojiPalette.Wrap($"✅ Added podcast '{podcastName}' with search term '{searchTerm}'", EmojiPalette.Confirm, profile.UseEmoji),
            cancellationToken);
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

    private static string FormatCommandLine(string command, string description, string emoji, bool useEmoji)
    {
        return $"{EmojiPrefix(emoji, useEmoji)}{command} - {description}";
    }

    private static async Task<bool> TryHandleNaturalPodcastPlayAsync(
        long chatId,
        string text,
        TelegramApiClient telegram,
        PodcastSubscriptionsService podcastSubscriptionsService,
        WebBrowserAssistantService webBrowserService,
        PersonalityProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        if (!lower.Contains("play", StringComparison.Ordinal) ||
            !lower.Contains("podcast", StringComparison.Ordinal))
        {
            return false;
        }

        var requestedEpisode = 1;
        var episodeMatch = Regex.Match(text, @"\bepisode\s+(\d{1,3})\b", RegexOptions.IgnoreCase);
        if (episodeMatch.Success && int.TryParse(episodeMatch.Groups[1].Value, out var parsedEpisode))
        {
            requestedEpisode = parsedEpisode;
        }

        var subscription = podcastSubscriptionsService.ResolveSubscription(text);
        if (subscription is null)
        {
            return false;
        }

        await telegram.SendMessageInChunksAsync(
            chatId,
            EmojiPalette.Wrap($"🎵 Playing {subscription.Name} episode {requestedEpisode}...", EmojiPalette.Music, profile.UseEmoji),
            cancellationToken);

        var playbackResult = await PlayPodcastEpisodeAsync(
            subscription.Name,
            requestedEpisode,
            podcastSubscriptionsService,
            webBrowserService);

        await telegram.SendMessageInChunksAsync(
            chatId,
            EmojiPalette.Wrap(playbackResult, EmojiPalette.Music, profile.UseEmoji),
            cancellationToken);

        return true;
    }

    private static string EmojiPrefix(string emoji, bool useEmoji)
    {
        return useEmoji ? $"{emoji} " : "- ";
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
