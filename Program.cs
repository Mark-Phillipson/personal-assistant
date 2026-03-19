using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DotNetEnv;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

Env.Load();

var environmentPersonality = PersonalityProfile.FromEnvironment();
var defaultPersonality = PersonalityProfile.LoadFromEnvironmentOrJson(environmentPersonality);

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
var talonUserDirectoryService = TalonUserDirectoryService.FromEnvironment();
var knownFolderExplorerService = KnownFolderExplorerService.FromEnvironment();
var telegramAttachmentService = TelegramAttachmentService.FromEnvironment();
var podcastSubscriptionsService = PodcastSubscriptionsService.FromEnvironmentOrJson();
var clipboardHistoryService = ClipboardHistoryService.FromEnvironment();
var assistantTools = AssistantToolsFactory.Build(gmailService, calendarService, naturalCommandsService, clipboardService, dadJokeService, webBrowserService, voiceAdminService, voiceAdminSearchService, talonUserDirectoryService, knownFolderExplorerService, podcastSubscriptionsService, clipboardHistoryService);

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
                cliPrompt,
                appCancellation.Token);
            break;

        case "terminal":
            await RunTerminalAsync(
                copilotClient,
                assistantTools,
                defaultPersonality,
                gmailService,
                calendarService,
                naturalCommandsService,
                clipboardService,
                voiceAdminService,
                voiceAdminSearchService,
                talonUserDirectoryService,
                knownFolderExplorerService,
                podcastSubscriptionsService,
                clipboardHistoryService,
                appCancellation.Token);
            break;

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

    if (args.Any(arg => string.Equals(arg, "--terminal", StringComparison.OrdinalIgnoreCase)))
    {
        return "terminal";
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

    if (string.Equals(configuredTransport, "terminal", StringComparison.OrdinalIgnoreCase))
    {
        return "terminal";
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

    // Shortcut: handle dad joke requests locally without going through the Copilot toolchain.
    if (Regex.IsMatch(prompt, "\\bdad\\s*joke\\b", RegexOptions.IgnoreCase))
    {
        var termMatch = Regex.Match(prompt, "\\bdad\\s*joke(?:\\s*(?:about|on|of)\\s+(.+))?$", RegexOptions.IgnoreCase);
        var term = termMatch.Success && termMatch.Groups.Count > 1 ? termMatch.Groups[1].Value.Trim() : null;
        var joke = await dadJokeService.GetJokeAsync(string.IsNullOrWhiteSpace(term) ? null : term, cancellationToken);
        Console.WriteLine(joke);
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
                        podcastSubscriptionsService,
                        clipboardHistoryService,
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

static async Task RunTerminalAsync(
    CopilotClient copilotClient,
    ICollection<AIFunction> assistantTools,
    PersonalityProfile defaultPersonality,
    GmailAssistantService gmailService,
    GoogleCalendarAssistantService calendarService,
    NaturalCommandsAssistantService naturalCommandsService,
    ClipboardAssistantService clipboardService,
    VoiceAdminService voiceAdminService,
    VoiceAdminSearchService voiceAdminSearchService,
    TalonUserDirectoryService talonUserDirectoryService,
    KnownFolderExplorerService knownFolderExplorerService,
    PodcastSubscriptionsService podcastSubscriptionsService,
    ClipboardHistoryService clipboardHistoryService,
    CancellationToken cancellationToken)
{
    Console.WriteLine("Terminal Copilot assistant started. Type /help for commands, /exit to quit.");
    Console.WriteLine($"Personality: {defaultPersonality.Name} | Tone: {defaultPersonality.Tone} | Emoji: {(defaultPersonality.UseEmoji ? defaultPersonality.EmojiDensity : "Off")}");
    Console.WriteLine($"Gmail tools: {(gmailService.IsConfigured ? "configured" : "not configured")}.");
    Console.WriteLine($"NaturalCommands: {(naturalCommandsService.IsConfigured ? "configured" : "not configured")}.");
    Console.WriteLine($"Clipboard: {(clipboardService.IsSupported ? "configured" : "not supported on this host")}.");
    Console.WriteLine($"ClipboardHistory: {clipboardHistoryService.GetSetupStatusText()}");
    Console.WriteLine($"VoiceAdmin: {(voiceAdminService.IsConfigured ? "configured" : "not configured")}.");
    Console.WriteLine($"VoiceAdminSearch: {(voiceAdminSearchService.IsConfigured ? "configured" : "not configured")}.");
    Console.WriteLine($"TalonUserDir: {(talonUserDirectoryService.DirectoryExists ? "configured" : "not found")}. Root: {talonUserDirectoryService.RootPath}");
    Console.WriteLine($"KnownFolderExplorer: {knownFolderExplorerService.GetSetupStatusText()}");
    Console.WriteLine($"Podcasts: {podcastSubscriptionsService.GetSetupStatusText()}");

    var session = await CreateConfiguredSessionAsync(copilotClient, assistantTools, defaultPersonality);

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("> ");
            var incomingText = Console.ReadLine();
            if (incomingText is null)
            {
                break;
            }

            incomingText = incomingText.Trim();
            if (string.IsNullOrWhiteSpace(incomingText))
            {
                continue;
            }

            if (string.Equals(incomingText, "/exit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(incomingText, "/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.Equals(incomingText, "/help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Commands: /help, /reset, /gmail-status, /calendar-status, /clipboard-status, /natural <command>, /nc <command>, /exit");
                continue;
            }

            if (string.Equals(incomingText, "/gmail-status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(gmailService.GetSetupStatusText());
                continue;
            }

            if (string.Equals(incomingText, "/calendar-status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(calendarService.GetSetupStatusText());
                continue;
            }

            if (string.Equals(incomingText, "/clipboard-status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(clipboardService.GetSetupStatusText());
                continue;
            }

            if (string.Equals(incomingText, "/reset", StringComparison.OrdinalIgnoreCase))
            {
                await session.DisposeAsync();
                session = await CreateConfiguredSessionAsync(copilotClient, assistantTools, defaultPersonality);

                Console.WriteLine("Session reset.");
                continue;
            }

            if (incomingText.StartsWith("/natural", StringComparison.OrdinalIgnoreCase)
                || incomingText.StartsWith("/nc", StringComparison.OrdinalIgnoreCase))
            {
                var commandPayload = ExtractCommandPayload(incomingText);
                var naturalCommandResult = await naturalCommandsService.ExecuteAsync(commandPayload, cancellationToken);
                Console.WriteLine(naturalCommandResult.Message);
                continue;
            }

            try
            {
                var assistantReply = await session.SendAndWaitAsync(new MessageOptions { Prompt = incomingText });
                var content = assistantReply?.Data.Content?.Trim();
                Console.WriteLine(string.IsNullOrWhiteSpace(content)
                    ? "I could not generate a response. Please try again."
                    : content);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[copilot.session.error] {ex.Message}");
                Console.WriteLine("I hit an error while generating a reply. Please try again.");
            }
        }
    }
    finally
    {
        await session.DisposeAsync();
    }
}

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

static string ExtractCommandPayload(string text)
{
    var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
    return parts.Length < 2 ? string.Empty : parts[1];
}
