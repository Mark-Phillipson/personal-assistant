using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
        DadJokeService dadJokeService,
        WebBrowserAssistantService webBrowserService,
        VoiceAdminService voiceAdminService,
        PodcastSubscriptionsService podcastSubscriptionsService,
        ClipboardHistoryService clipboardHistoryService,
        TextToSpeechService textToSpeechService,
        KnownFolderExplorerService knownFolderExplorerService,
        CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var profile = GetPersonalityForChat(chatId, personalityProfiles, defaultPersonality);

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
            await telegram.SendMessageInChunksAsync(chatId, naturalContent, cancellationToken);
            await textToSpeechService.TrySpeakPreviewAsync(naturalResult.Message, cancellationToken);
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
                        await telegram.SendMessageInChunksAsync(
                            chatId,
                            EmojiPalette.Wrap(joke, EmojiPalette.Happy, profile.UseEmoji),
                            cancellationToken);

                        try
                        {
                            await textToSpeechService.TrySpeakPreviewAsync(joke, cancellationToken);
                        }
                        catch (Exception ttsEx)
                        {
                            Console.Error.WriteLine($"[tts.error] Dad joke speak failed: {ttsEx.Message}");
                        }

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
                    await telegram.SendMessageInChunksAsync(chatId, naturalContent, cancellationToken);
                    await textToSpeechService.TrySpeakPreviewAsync(naturalResult.Message, cancellationToken);
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
        await telegram.SendMessageInChunksAsync(
            chatId,
            EmojiPalette.Wrap(dadJoke, EmojiPalette.Happy, profile.UseEmoji),
            cancellationToken);

        try
        {
            await textToSpeechService.TrySpeakPreviewAsync(dadJoke, cancellationToken);
        }
        catch (Exception ttsEx)
        {
            Console.Error.WriteLine($"[tts.error] Dad joke speak failed: {ttsEx.Message}");
        }

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

        // Deterministic path for conversational todo-list requests to avoid model hallucinations.
        if (storedAttachment is null && LooksLikeTodoListRequest(text) && voiceAdminService.IsConfigured)
        {
            if (LooksLikeTodoCsvExportRequest(text))
            {
                var exportResult = await ExportVoiceAdminTodosToCsvAsync(text, voiceAdminService);
                await telegram.SendMessageInChunksAsync(chatId, EmojiPalette.Wrap(exportResult, EmojiPalette.Confirm, profile.UseEmoji), cancellationToken);
                return;
            }

            var todoTable = await voiceAdminService.ListIncompleteTodosAsync(asHtmlTable: true);
            await telegram.SendMessageInChunksAsync(chatId, todoTable, cancellationToken);
            return;
        }

        var messageOptions = BuildMessageOptions(text, storedAttachment);

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
                cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                content = "I could not generate a response. Please try again.";
            }

            content = await ReconcileTodoEmptyClaimAsync(content, voiceAdminService);

            await telegram.SendMessageInChunksAsync(chatId, content, cancellationToken);

            try
            {
                Console.Error.WriteLine($"[tts.info] Main chat response will be spoken (if enabled).");
                await textToSpeechService.TrySpeakPreviewAsync(content, cancellationToken);
            }
            catch (Exception ttsEx)
            {
                Console.Error.WriteLine($"[tts.error] Main chat speak failed: {ttsEx.Message}");
            }
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
        TelegramApiClient telegramClient,
        KnownFolderExplorerService folderService,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools,
        PersonalityProfile profile)
    {
        if (sessions.TryGetValue(chatId, out var existingSession))
        {
            return existingSession;
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

        var createdSession = await copilotClient.CreateSessionAsync(new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = sessionTools,
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
        TelegramApiClient telegram,
        KnownFolderExplorerService knownFolderExplorerService,
        CopilotClient copilotClient,
        ConcurrentDictionary<long, CopilotSession> sessions,
        ICollection<AIFunction> assistantTools,
        PersonalityProfile profile,
        CancellationToken cancellationToken)
    {
        var session = await GetOrCreateSessionAsync(chatId, telegram, knownFolderExplorerService, copilotClient, sessions, assistantTools, profile);

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

            var recreatedSession = await GetOrCreateSessionAsync(chatId, telegram, knownFolderExplorerService, copilotClient, sessions, assistantTools, profile);
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
        CancellationToken cancellationToken)
    {
        var messageOptions = new MessageOptions { Prompt = commandPayload };

        var content = await SendWithSessionRecoveryAsync(
            chatId,
            messageOptions,
            telegram,
            knownFolderExplorerService,
            copilotClient,
            sessions,
            assistantTools,
            profile,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            content = "I could not generate a response. Please try again.";
        }

        content = await ReconcileTodoEmptyClaimAsync(content, voiceAdminService);

        await telegram.SendMessageInChunksAsync(chatId, content, cancellationToken);

        try
        {
            await textToSpeechService.TrySpeakPreviewAsync(content, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tts.speak.error] chat={chatId} {ex}");
        }
    }

    private static bool LooksLikeTodoListRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var mentionsTodo = Regex.IsMatch(text, "\\b(todos?|to\\s*do|task|tasks|pending|open items?)\\b", RegexOptions.IgnoreCase);
        var asksToList = Regex.IsMatch(text, "\\b(list|show|table|what|check|left|still|open)\\b", RegexOptions.IgnoreCase);
        return mentionsTodo && asksToList;
    }

    private static bool LooksLikeNoTodoClaim(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return Regex.IsMatch(content, "\\b(no|none|empty)\\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, "\\b(todos?|to\\s*do|task|tasks)\\b", RegexOptions.IgnoreCase);
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
