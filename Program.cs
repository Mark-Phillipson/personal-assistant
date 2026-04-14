using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetEnv;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

var workingDirectoryEnvFilePath = Path.Combine(Environment.CurrentDirectory, ".env");
var assemblyDirectoryEnvFilePath = Path.Combine(AppContext.BaseDirectory, ".env");

if (File.Exists(workingDirectoryEnvFilePath))
{
    try
    {
        Env.Load(workingDirectoryEnvFilePath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[env.load] Failed to parse {workingDirectoryEnvFilePath}: {ex.Message}");
    }
}
else if (File.Exists(assemblyDirectoryEnvFilePath))
{
    try
    {
        Env.Load(assemblyDirectoryEnvFilePath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[env.load] Failed to parse {assemblyDirectoryEnvFilePath}: {ex.Message}");
    }
}

// Ensure ASSISTANT_MODEL comes from the .env configuration file when present,
// so editor/terminal process variables cannot silently override model selection.
var modelFromDotEnv =
    TryReadDotEnvValue(workingDirectoryEnvFilePath, "ASSISTANT_MODEL")
    ?? TryReadDotEnvValue(assemblyDirectoryEnvFilePath, "ASSISTANT_MODEL");

if (!string.IsNullOrWhiteSpace(modelFromDotEnv))
{
    Environment.SetEnvironmentVariable("ASSISTANT_MODEL", modelFromDotEnv.Trim());
}

var environmentPersonality = PersonalityProfile.FromEnvironment();
var defaultPersonality = PersonalityProfile.LoadFromEnvironmentOrJson(environmentPersonality);

if (args.Any(arg => string.Equals(arg, "--run-phase3-tests", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("Running Phase 3 tests...");
    PhaseThreeTestRunner.RunAll();
    return;
}

var assistantTransport = ResolveAssistantTransport(args);
var cliPrompt = ExtractCliPrompt(args);

var gmailService = GmailAssistantService.FromEnvironment();
var calendarService = GoogleCalendarAssistantService.FromEnvironment();
var naturalCommandsService = NaturalCommandsAssistantService.FromEnvironment();
var clipboardService = ClipboardAssistantService.FromEnvironment();
var tickerNotificationService = TickerNotificationService.FromEnvironment();
var dadJokeService = new DadJokeService();
var webBrowserService = WebBrowserAssistantService.FromEnvironment(clipboardService);
var voiceAdminService = VoiceAdminService.FromEnvironment();
var voiceAdminSearchService = VoiceAdminSearchService.FromEnvironment();
var windowsFocusAssistService = WindowsFocusAssistService.FromEnvironment();
var databaseRegistry = DatabaseRegistry.FromEnvironment();
var genericDatabaseService = new GenericDatabaseService(databaseRegistry);
var talonUserDirectoryService = TalonUserDirectoryService.FromEnvironment();
var knownFolderExplorerService = KnownFolderExplorerService.FromEnvironment();
var telegramAttachmentService = TelegramAttachmentService.FromEnvironment();
var podcastSubscriptionsService = PodcastSubscriptionsService.FromEnvironmentOrJson();
var clipboardHistoryService = ClipboardHistoryService.FromEnvironment();
var gitHubTodosService = GitHubTodosService.FromEnvironment();
var telegramChatIdStore = TelegramChatIdStore.FromEnvironment();

// CLI helpers for Gmail auth flows
if (args.Any(arg => string.Equals(arg, "--print-gmail-consent-url", StringComparison.OrdinalIgnoreCase)))
{
    try
    {
        var url = await gmailService.GetConsentUrlAsync();
        Console.WriteLine(url);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[gmail.auth] Failed to build consent URL: {ex.Message}");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Any(arg => string.Equals(arg, "--start-gmail-auth", StringComparison.OrdinalIgnoreCase)))
{
    try
    {
        await gmailService.StartInteractiveAuthAsync();
        Console.WriteLine("Gmail interactive auth completed (credentials saved to token store).");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[gmail.auth] Interactive auth failed: {ex.Message}");
        Environment.ExitCode = 1;
    }
    return;
}

// Initialize pronunciation dictionary service for TTS corrections.
var pronunciationDictionaryPath = EnvironmentSettings.ReadOptionalString("TTS_PRONUNCIATION_DICT_PATH") 
    ?? Path.Combine(Environment.CurrentDirectory, "pronunciation-corrections.json");
var pronunciationService = new PronunciationDictionaryService(pronunciationDictionaryPath);

var textToSpeechService = TextToSpeechService.FromEnvironment(pronunciationService);
Console.WriteLine(databaseRegistry.GetSetupStatusText());
Console.WriteLine($"GenericDatabaseService has {genericDatabaseService.ListSources().Count} source(s) available.");

var assistantTools = AssistantToolsFactory.Build(gmailService, calendarService, naturalCommandsService, clipboardService, tickerNotificationService, dadJokeService, webBrowserService, voiceAdminService, voiceAdminSearchService, windowsFocusAssistService, genericDatabaseService, talonUserDirectoryService, knownFolderExplorerService, podcastSubscriptionsService, clipboardHistoryService, gitHubTodosService);

await using var copilotClient = new CopilotClient();
await using var webBrowserDisposable = webBrowserService;

// Load pronunciation corrections from dictionary file on startup.
await pronunciationService.LoadFromFileAsync(default);

// Remove duplicate clipboard history entries on startup
await clipboardHistoryService.RemoveDuplicateEntriesAsync(default);

// Cleanup old clipboard history entries on startup
await clipboardHistoryService.CleanupOldEntriesAsync(default);

// Start clipboard monitor for recording manual copy events
clipboardHistoryService.StartMonitoring();

using var appCancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    appCancellation.Cancel();
};

using var apiServerCancellation = new CancellationTokenSource();
var apiServerTask = CommandApiServer.StartAsync(
    apiServerCancellation.Token,
    aiFallback: (command, ct) => HandleAndroidCompanionFallbackAsync(
        command,
        ct,
        copilotClient,
        assistantTools,
        defaultPersonality,
        telegramChatIdStore));

try
{
    if (assistantTransport == "cli")
    {
        await RunCliAsync(
            copilotClient,
            assistantTools,
            defaultPersonality,
            dadJokeService,
            telegramChatIdStore,
            textToSpeechService,
            cliPrompt,
            appCancellation.Token);
    }
    else
    {
        await RunTelegramAsync(
            copilotClient,
            assistantTools,
            defaultPersonality,
            environmentPersonality,
            gmailService,
            calendarService,
            naturalCommandsService,
            clipboardService,
            dadJokeService,
            webBrowserService,
            voiceAdminService,
            voiceAdminSearchService,
            talonUserDirectoryService,
            knownFolderExplorerService,
            telegramAttachmentService,
            podcastSubscriptionsService,
            clipboardHistoryService,
            telegramChatIdStore,
            textToSpeechService,
            pronunciationService,
            tickerNotificationService,
            appCancellation.Token);
    }
}
finally
{
    clipboardHistoryService.StopMonitoring();
    apiServerCancellation.Cancel();
    try
    {
        await apiServerTask;
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[command-api] shutdown error: {ex.Message}");
    }
}

static string ResolveAssistantTransport(string[] args)
{
    if (args.Any(arg => string.Equals(arg, "--cli", StringComparison.OrdinalIgnoreCase)))
    {
        return "cli";
    }

    if (args.Any(arg => string.Equals(arg, "--telegram", StringComparison.OrdinalIgnoreCase)))
    {
        return "telegram";
    }

    var configuredTransport = Environment.GetEnvironmentVariable("ASSISTANT_TRANSPORT")?.Trim();
    if (string.Equals(configuredTransport, "cli", StringComparison.OrdinalIgnoreCase))
    {
        return "cli";
    }

    return "telegram";
}

static string ExtractCliPrompt(string[] args)
{
    var cliIndex = Array.FindIndex(args, arg => string.Equals(arg, "--cli", StringComparison.OrdinalIgnoreCase));
    if (cliIndex < 0)
    {
        return string.Empty;
    }

    if (cliIndex >= args.Length - 1)
    {
        return string.Empty;
    }

    return string.Join(' ', args[(cliIndex + 1)..]).Trim();
}

static async Task RunCliAsync(
    CopilotClient copilotClient,
    ICollection<AIFunction> assistantTools,
    PersonalityProfile defaultPersonality,
    DadJokeService dadJokeService,
    TelegramChatIdStore telegramChatIdStore,
    TextToSpeechService textToSpeechService,
    string prompt,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(prompt))
    {
        Console.Error.WriteLine("Usage: personal-assistant --cli \"<prompt>\"");
        Console.Error.WriteLine("Example: personal-assistant --cli \"bob please play Ukraine the latest podcast\"");
        Environment.ExitCode = 2;
        return;
    }

    var telegramToken = EnvironmentSettings.ReadOptionalString("TELEGRAM_BOT_TOKEN");
    var storedChatId = telegramToken is not null ? await telegramChatIdStore.LoadAsync() : null;
    using var telegram = storedChatId.HasValue && telegramToken is not null
        ? new TelegramApiClient(telegramToken)
        : null;

    // Echo the recognized command to Telegram so the user can confirm speech recognition.
    if (telegram is not null && storedChatId.HasValue)
    {
        await telegram.SendMessageInChunksAsync(storedChatId.Value, $"🎤 <b>Voice command:</b> {System.Net.WebUtility.HtmlEncode(prompt)}", cancellationToken);
    }
    // TTS-only CLI smoke test.
    if (string.Equals(prompt.Trim(), "tts test", StringComparison.OrdinalIgnoreCase))
    {
        var testPhrase = "This is a text to speech test phrase. You should hear it on your default Windows audio device.";
        Console.WriteLine("Running TTS test...");

        try
        {
            Console.WriteLine("TTS test: calling TrySpeakPreviewAsync...");
            await textToSpeechService.TrySpeakPreviewAsync(testPhrase, cancellationToken);
            Console.WriteLine("TTS test completed: spoken phrase invoked.");
            Console.WriteLine("TTS test: Completed internal TTS call.");

            if (telegram is not null && storedChatId.HasValue)
            {
                await telegram.SendMessageInChunksAsync(storedChatId.Value, "🗣️ TTS test completed. Check your headphones.", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TTS test error: {ex.Message}");
            Console.WriteLine(ex.ToString());
            if (telegram is not null && storedChatId.HasValue)
            {
                await telegram.SendMessageInChunksAsync(storedChatId.Value, $"⚠️ TTS test failed: {ex.Message}", cancellationToken);
            }
        }

        return;
    }
    // Shortcut: handle dad joke requests locally without going through the Copilot toolchain.
    if (Regex.IsMatch(prompt, "\\bdad\\s*joke\\b", RegexOptions.IgnoreCase))
    {
        var termMatch = Regex.Match(prompt, "\\bdad\\s*joke(?:\\s*(?:about|on|of)\\s+(.+))?$", RegexOptions.IgnoreCase);
        var term = termMatch.Success && termMatch.Groups.Count > 1 ? termMatch.Groups[1].Value.Trim() : null;
        var joke = await dadJokeService.GetJokeAsync(string.IsNullOrWhiteSpace(term) ? null : term, cancellationToken);
        Console.WriteLine(joke);
        if (telegram is not null && storedChatId.HasValue)
        {
            await telegram.SendMessageInChunksAsync(storedChatId.Value, joke, cancellationToken);
        }
        try
        {
            await textToSpeechService.TrySpeakPreviewAsync(joke, cancellationToken, true);
        }
        catch (Exception ttsEx)
        {
            Console.Error.WriteLine($"[tts.error] CLI dad joke speak failed: {ttsEx.Message}");
        }
        return;
    }

    var session = await CreateConfiguredSessionAsync(copilotClient, assistantTools, defaultPersonality);

    try
    {
        var assistantReply = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt }, null, cancellationToken);
        var content = assistantReply?.Data.Content?.Trim();

        if (string.IsNullOrWhiteSpace(content))
        {
            Console.WriteLine("I could not generate a response. Please try again.");
            Environment.ExitCode = 1;
            return;
        }

        content = Regex.Replace(
            content,
            "(?im)^\\s*out(?:\\s+.+)?\\s*$",
            $"Out {SystemPromptBuilder.GetConfiguredModel()}");

        Console.WriteLine(content);
        if (telegram is not null && storedChatId.HasValue)
        {
            await telegram.SendMessageInChunksAsync(storedChatId.Value, content, cancellationToken);
        }

        try
        {
            // CLI mode is typically triggered by voice commands (Talon/bob flow), so always run TTS.
            // If you want to make this optional, use an env var or explicit command.
            const bool forceTts = true;
            Console.Error.WriteLine($"[tts.info] CLI chat response will be spoken (if TTS enabled or forced={forceTts}).");
            await textToSpeechService.TrySpeakPreviewAsync(content, cancellationToken, forceTts);
        }
        catch (Exception ttsEx)
        {
            Console.Error.WriteLine($"[tts.error] CLI speak failed: {ttsEx.Message}");
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine("CLI request canceled.");
        Environment.ExitCode = 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[copilot.cli.error] {ex.Message}");
        Environment.ExitCode = 1;
    }
    finally
    {
        await session.DisposeAsync();
    }
}

    
static async Task RunTelegramAsync(
    CopilotClient copilotClient,
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
    VoiceAdminSearchService voiceAdminSearchService,
    TalonUserDirectoryService talonUserDirectoryService,
    KnownFolderExplorerService knownFolderExplorerService,
    TelegramAttachmentService telegramAttachmentService,
    PodcastSubscriptionsService podcastSubscriptionsService,
    ClipboardHistoryService clipboardHistoryService,
    TelegramChatIdStore telegramChatIdStore,
    TextToSpeechService textToSpeechService,
    PronunciationDictionaryService pronunciationService,
    TickerNotificationService tickerNotificationService,
    CancellationToken cancellationToken)
{
    var telegramToken = EnvironmentSettings.Require("TELEGRAM_BOT_TOKEN");
    var pollTimeoutSeconds = EnvironmentSettings.ReadInt("TELEGRAM_POLL_TIMEOUT_SECONDS", fallback: 25, min: 1, max: 50);
    var receiveBackoffSeconds = EnvironmentSettings.ReadInt("TELEGRAM_ERROR_BACKOFF_SECONDS", fallback: 3, min: 1, max: 30);

    using var telegram = new TelegramApiClient(telegramToken);
    var sessions = new ConcurrentDictionary<long, CopilotSession>();
    var conversationHistories = new ConcurrentDictionary<long, List<string>>();
    var personalityProfiles = new ConcurrentDictionary<long, PersonalityProfile>();

    Console.WriteLine("Telegram Copilot assistant started. Press Ctrl+C to stop.");
    Console.WriteLine($"Gmail tools: {(gmailService.IsConfigured ? "configured" : "not configured")}.");
    Console.WriteLine($"NaturalCommands: {(naturalCommandsService.IsConfigured ? "configured" : "not configured")}.");
    Console.WriteLine($"Clipboard: {(clipboardService.IsSupported ? "configured" : "not supported on this host")}.");
    Console.WriteLine($"ClipboardHistory: {clipboardHistoryService.GetSetupStatusText()}");
    Console.WriteLine($"VoiceAdmin: {(voiceAdminService.IsConfigured ? "configured" : "not configured")}.");
    Console.WriteLine($"VoiceAdminSearch: {(voiceAdminSearchService.IsConfigured ? "configured" : "not configured")}.");
    Console.WriteLine($"TalonUserDir: {(talonUserDirectoryService.DirectoryExists ? "configured" : "not found")}. Root: {talonUserDirectoryService.RootPath}");
    Console.WriteLine($"KnownFolderExplorer: {knownFolderExplorerService.GetSetupStatusText()}");
    Console.WriteLine($"TelegramAttachments: Root={telegramAttachmentService.RootPath} MaxBytes={telegramAttachmentService.MaxAttachmentBytes}.");
    Console.WriteLine($"Podcasts: {podcastSubscriptionsService.GetSetupStatusText()}");

    long? nextOffset = null;

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<TelegramUpdate> updates;

            try
            {
                updates = await telegram.GetUpdatesAsync(nextOffset, pollTimeoutSeconds, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[telegram.receive.error] {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(receiveBackoffSeconds), cancellationToken);
                continue;
            }

            foreach (var update in updates)
            {
                nextOffset = update.UpdateId + 1;

                if (update.Message is not { } incomingMessage)
                {
                    continue;
                }

                var incomingText = incomingMessage.Text?.Trim() ?? incomingMessage.Caption?.Trim() ?? string.Empty;
                var hasAttachment = incomingMessage.Document is not null || (incomingMessage.Photo?.Count ?? 0) > 0;

                if (string.IsNullOrWhiteSpace(incomingText) && !hasAttachment)
                {
                    continue;
                }

                await telegramChatIdStore.SaveAsync(incomingMessage.Chat.Id);

                try
                {
                    await TelegramMessageHandler.HandleAsync(
                        incomingMessage,
                        incomingText,
                        telegram,
                        telegramAttachmentService,
                        copilotClient,
                        sessions,
                        personalityProfiles,
                        assistantTools,
                        defaultPersonality,
                        environmentPersonality,
                        gmailService,
                        calendarService,
                        naturalCommandsService,
                        clipboardService,
                        tickerNotificationService,
                        dadJokeService,
                        webBrowserService,
                        voiceAdminService,
                        GitHubTodosService.FromEnvironment(),
                        podcastSubscriptionsService,
                        clipboardHistoryService,
                        textToSpeechService,
                        pronunciationService,
                        knownFolderExplorerService,
                        conversationHistories,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[telegram.handle.error] update={update.UpdateId} chat={incomingMessage.Chat.Id} {ex}");
                }
            }
        }
    }
    finally
    {
        foreach (var session in sessions.Values)
        {
            await session.DisposeAsync();
        }
    }
}

var debugLogPath = Path.Combine(AppContext.BaseDirectory, "assistant-cli-debug.log");

static async Task<CopilotSession> CreateConfiguredSessionAsync(
    CopilotClient copilotClient,
    ICollection<AIFunction> assistantTools,
    PersonalityProfile profile)
{
    var configuredModel = SystemPromptBuilder.GetConfiguredModel();
    var session = await copilotClient.CreateSessionAsync(new SessionConfig
    {
        OnPermissionRequest = PermissionHandler.ApproveAll,
        Model = configuredModel,
        Tools = assistantTools,
        SystemMessage = new SystemMessageConfig
        {
            Content = SystemPromptBuilder.Build(profile)
        }
    });

    await ModelSelectionGuard.EnsureSessionUsesConfiguredModelAsync(
        session,
        configuredModel,
        context: "cli",
        CancellationToken.None);

    return session;
}

static async Task<CommandApiServer.AiFallbackResponse> HandleAndroidCompanionFallbackAsync(
    string command,
    CancellationToken cancellationToken,
    CopilotClient copilotClient,
    ICollection<AIFunction> assistantTools,
    PersonalityProfile defaultPersonality,
    TelegramChatIdStore telegramChatIdStore)
{
    var telegramToken = EnvironmentSettings.ReadOptionalString("TELEGRAM_BOT_TOKEN");
    var storedChatId = telegramToken is not null ? await telegramChatIdStore.LoadAsync() : null;
    using var telegram = storedChatId.HasValue && telegramToken is not null
        ? new TelegramApiClient(telegramToken)
        : null;

    if (telegram is not null && storedChatId.HasValue)
    {
        await telegram.SendMessageInChunksAsync(
            storedChatId.Value,
            $"📱 <b>Android command:</b> {System.Net.WebUtility.HtmlEncode(command)}",
            cancellationToken);
    }

    var deviceActions = new List<CommandApiServer.CommandAction>();
    var trackedToolCalls = new HashSet<string>(StringComparer.Ordinal);
    var syncRoot = new object();

    var session = await CreateConfiguredSessionAsync(copilotClient, assistantTools, defaultPersonality);
    using var sessionSubscription = session.On(evt =>
    {
        switch (evt)
        {
            case ToolExecutionStartEvent start
                when string.Equals(start.Data?.ToolName, "execute_device_action", StringComparison.Ordinal):
                if (!string.IsNullOrWhiteSpace(start.Data.ToolCallId))
                {
                    lock (syncRoot)
                    {
                        trackedToolCalls.Add(start.Data.ToolCallId);
                    }
                }
                break;

            case ToolExecutionCompleteEvent complete:
                if (complete.Data is null || !complete.Data.Success || string.IsNullOrWhiteSpace(complete.Data.ToolCallId))
                {
                    break;
                }

                lock (syncRoot)
                {
                    if (!trackedToolCalls.Contains(complete.Data.ToolCallId))
                    {
                        return;
                    }
                }

                if (TryExtractDeviceActions(complete.Data.Result, out var extractedActions))
                {
                    lock (syncRoot)
                    {
                        deviceActions.AddRange(extractedActions);
                    }
                }
                break;
        }
    });

    try
    {
        var reply = await session.SendAndWaitAsync(new MessageOptions { Prompt = command }, null, cancellationToken);
        var content = reply?.Data.Content?.Trim();
        List<CommandApiServer.CommandAction> actionsSnapshot;

        lock (syncRoot)
        {
            if (string.IsNullOrWhiteSpace(content) && deviceActions.Count > 0)
            {
                content = "Prepared an action for your phone.";
            }

            content ??= "No response from assistant.";
            actionsSnapshot = [.. deviceActions];
        }

        if (telegram is not null && storedChatId.HasValue)
        {
            await telegram.SendMessageInChunksAsync(storedChatId.Value, content, cancellationToken);
        }

        Console.WriteLine($"[command-api] AI response sent to Telegram: {content[..Math.Min(80, content.Length)]}... | Copilot actions: {actionsSnapshot.Count}");
        return new CommandApiServer.AiFallbackResponse(content, actionsSnapshot, true);
    }
    finally
    {
        await session.DisposeAsync();
    }
}

static bool TryExtractDeviceActions(
    ToolExecutionCompleteDataResult? result,
    out List<CommandApiServer.CommandAction> actions)
{
    actions = [];

    if (result is null)
    {
        return false;
    }

    foreach (var payload in EnumerateToolResultPayloads(result))
    {
        if (TryParseDeviceActionPayload(payload, out var action))
        {
            actions.Add(action);
        }
    }

    return actions.Count > 0;
}

static IEnumerable<string> EnumerateToolResultPayloads(ToolExecutionCompleteDataResult result)
{
    if (!string.IsNullOrWhiteSpace(result.Content))
    {
        yield return result.Content;
    }

    if (!string.IsNullOrWhiteSpace(result.DetailedContent)
        && !string.Equals(result.DetailedContent, result.Content, StringComparison.Ordinal))
    {
        yield return result.DetailedContent;
    }

    if (result.Contents is null)
    {
        yield break;
    }

    foreach (var item in result.Contents)
    {
        switch (item)
        {
            case ToolExecutionCompleteDataResultContentsItemText textItem when !string.IsNullOrWhiteSpace(textItem.Text):
                yield return textItem.Text;
                break;

            case ToolExecutionCompleteDataResultContentsItemResource resourceItem when resourceItem.Resource is not null:
                if (resourceItem.Resource is JsonElement jsonElement)
                {
                    yield return jsonElement.GetRawText();
                }
                else
                {
                    yield return resourceItem.Resource.ToString() ?? string.Empty;
                }
                break;
        }
    }
}

static bool TryParseDeviceActionPayload(string payload, out CommandApiServer.CommandAction action)
{
    action = null!;

    if (string.IsNullOrWhiteSpace(payload))
    {
        return false;
    }

    try
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!document.RootElement.TryGetProperty("actionType", out var actionTypeElement)
            || actionTypeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var actionType = actionTypeElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return false;
        }

        Dictionary<string, string>? parameters = null;
        if (document.RootElement.TryGetProperty("parameters", out var parametersElement)
            && parametersElement.ValueKind == JsonValueKind.Object)
        {
            parameters = [];
            foreach (var property in parametersElement.EnumerateObject())
            {
                parameters[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }
        }

        action = new CommandApiServer.CommandAction(actionType, parameters);
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

static string? TryReadDotEnvValue(string envFilePath, string key)
{
    if (!File.Exists(envFilePath))
    {
        return null;
    }

    foreach (var rawLine in File.ReadLines(envFilePath))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
        {
            continue;
        }

        if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            line = line[7..].TrimStart();
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var parsedKey = line[..separatorIndex].Trim();
        if (!parsedKey.Equals(key, StringComparison.Ordinal))
        {
            continue;
        }

        var parsedValue = line[(separatorIndex + 1)..].Trim();

        if (parsedValue.Length >= 2
            && ((parsedValue.StartsWith('"') && parsedValue.EndsWith('"'))
                || (parsedValue.StartsWith('\'') && parsedValue.EndsWith('\''))))
        {
            parsedValue = parsedValue[1..^1];
        }

        return parsedValue;
    }

    return null;
}
