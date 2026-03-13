using System.Collections.Concurrent;
using DotNetEnv;
using GitHub.Copilot.SDK;

Env.Load();

var telegramToken = EnvironmentSettings.Require("TELEGRAM_BOT_TOKEN");
var pollTimeoutSeconds = EnvironmentSettings.ReadInt("TELEGRAM_POLL_TIMEOUT_SECONDS", fallback: 25, min: 1, max: 50);
var receiveBackoffSeconds = EnvironmentSettings.ReadInt("TELEGRAM_ERROR_BACKOFF_SECONDS", fallback: 3, min: 1, max: 30);

var gmailService = GmailAssistantService.FromEnvironment();
var calendarService = GoogleCalendarAssistantService.FromEnvironment();
var assistantTools = AssistantToolsFactory.Build(gmailService, calendarService);

using var telegram = new TelegramApiClient(telegramToken);
await using var copilotClient = new CopilotClient();
var sessions = new ConcurrentDictionary<long, CopilotSession>();

using var appCancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    appCancellation.Cancel();
};

Console.WriteLine("Telegram Copilot assistant started. Press Ctrl+C to stop.");
Console.WriteLine($"Gmail tools: {(gmailService.IsConfigured ? "configured" : "not configured")}.");

long? nextOffset = null;

try
{
    while (!appCancellation.IsCancellationRequested)
    {
        IReadOnlyList<TelegramUpdate> updates;

        try
        {
            updates = await telegram.GetUpdatesAsync(nextOffset, pollTimeoutSeconds, appCancellation.Token);
        }
        catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[telegram.receive.error] {ex.Message}");
            await Task.Delay(TimeSpan.FromSeconds(receiveBackoffSeconds), appCancellation.Token);
            continue;
        }

        foreach (var update in updates)
        {
            nextOffset = update.UpdateId + 1;

            if (update.Message?.Text is not string incomingText || string.IsNullOrWhiteSpace(incomingText))
            {
                continue;
            }

            await TelegramMessageHandler.HandleAsync(
                update.Message,
                incomingText.Trim(),
                telegram,
                copilotClient,
                sessions,
                assistantTools,
                gmailService,
                calendarService,
                appCancellation.Token);
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
