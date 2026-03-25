using System.Collections.Concurrent;
using System.IO;
using System.Net;
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
var dadJokeService = new DadJokeService();
var webBrowserService = WebBrowserAssistantService.FromEnvironment(clipboardService);
var voiceAdminService = VoiceAdminService.FromEnvironment();
var voiceAdminSearchService = VoiceAdminSearchService.FromEnvironment();
var databaseRegistry = DatabaseRegistry.FromEnvironment();
var genericDatabaseService = new GenericDatabaseService(databaseRegistry);
var talonUserDirectoryService = TalonUserDirectoryService.FromEnvironment();
var knownFolderExplorerService = KnownFolderExplorerService.FromEnvironment();
var telegramAttachmentService = TelegramAttachmentService.FromEnvironment();
var podcastSubscriptionsService = PodcastSubscriptionsService.FromEnvironmentOrJson();
var clipboardHistoryService = ClipboardHistoryService.FromEnvironment();
var telegramChatIdStore = TelegramChatIdStore.FromEnvironment();
var textToSpeechService = TextToSpeechService.FromEnvironment();
Console.WriteLine(databaseRegistry.GetSetupStatusText());
Console.WriteLine($"GenericDatabaseService has {genericDatabaseService.ListSources().Count} source(s) available.");

var assistantTools = AssistantToolsFactory.Build(gmailService, calendarService, naturalCommandsService, clipboardService, dadJokeService, webBrowserService, voiceAdminService, voiceAdminSearchService, genericDatabaseService, talonUserDirectoryService, knownFolderExplorerService, podcastSubscriptionsService, clipboardHistoryService);

await using var copilotClient = new CopilotClient();
await using var webBrowserDisposable = webBrowserService;

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

try
{
    switch (assistantTransport)
    {
        case "cli":
            await RunCliAsync(
                copilotClient,
                assistantTools,
                defaultPersonality,
                dadJokeService,
                telegramChatIdStore,
                textToSpeechService,
                cliPrompt,
                appCancellation.Token);
            break;

        // Terminal transport removed — use CLI or Telegram transports only.

        default:
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
                appCancellation.Token);
            break;
    }
}
finally
{
    clipboardHistoryService.StopMonitoring();
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
    CancellationToken cancellationToken)
{
    var telegramToken = EnvironmentSettings.Require("TELEGRAM_BOT_TOKEN");
    var pollTimeoutSeconds = EnvironmentSettings.ReadInt("TELEGRAM_POLL_TIMEOUT_SECONDS", fallback: 25, min: 1, max: 50);
    var receiveBackoffSeconds = EnvironmentSettings.ReadInt("TELEGRAM_ERROR_BACKOFF_SECONDS", fallback: 3, min: 1, max: 30);

    using var telegram = new TelegramApiClient(telegramToken);
    var sessions = new ConcurrentDictionary<long, CopilotSession>();
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
                        dadJokeService,
                        webBrowserService,
                        voiceAdminService,
                        podcastSubscriptionsService,
                        clipboardHistoryService,
                        textToSpeechService,
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

static Task<CopilotSession> CreateConfiguredSessionAsync(
    CopilotClient copilotClient,
    ICollection<AIFunction> assistantTools,
    PersonalityProfile profile)
{
    return copilotClient.CreateSessionAsync(new SessionConfig
    {
        OnPermissionRequest = PermissionHandler.ApproveAll,
        Tools = assistantTools,
        SystemMessage = new SystemMessageConfig
        {
            Content = SystemPromptBuilder.Build(profile)
        }
    });
}
