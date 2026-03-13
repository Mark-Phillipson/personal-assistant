using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetEnv;
using GitHub.Copilot.SDK;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.AI;

Env.Load();

var telegramToken = RequireEnvironmentVariable("TELEGRAM_BOT_TOKEN");
var pollTimeoutSeconds = ReadIntEnvironmentVariable("TELEGRAM_POLL_TIMEOUT_SECONDS", fallback: 25, min: 1, max: 50);
var receiveBackoffSeconds = ReadIntEnvironmentVariable("TELEGRAM_ERROR_BACKOFF_SECONDS", fallback: 3, min: 1, max: 30);

var gmailService = GmailAssistantService.FromEnvironment();
var calendarService = GoogleCalendarAssistantService.FromEnvironment();
var assistantTools = BuildAssistantTools(gmailService, calendarService);

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

            await HandleMessageAsync(
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

static List<AIFunction> BuildAssistantTools(GmailAssistantService gmailService, GoogleCalendarAssistantService calendarService)
{
    return
    [
        // Gmail tools
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
        // Calendar tools
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
            "Create a new event in Google Calendar with summary, description, start, and end times.")
    ];
}

static async Task HandleMessageAsync(
    TelegramMessage message,
    string text,
    TelegramApiClient telegram,
    CopilotClient copilotClient,
    ConcurrentDictionary<long, CopilotSession> sessions,
    ICollection<AIFunction> assistantTools,
    GmailAssistantService gmailService,
    GoogleCalendarAssistantService calendarService,
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
                    "Commands:\n/start - welcome message\n/help - show commands\n/reset - reset your Copilot session\n/gmail-status - check Gmail setup\n/calendar-status - check Google Calendar setup\n/calendar-events - list upcoming Google Calendar events\n/calendar-create - create a new Google Calendar event",
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

static async Task<CopilotSession> GetOrCreateSessionAsync(
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

static string RequireEnvironmentVariable(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (!string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    throw new InvalidOperationException($"Required environment variable '{name}' is missing.");
}

static int ReadIntEnvironmentVariable(string name, int fallback, int min, int max)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw))
    {
        return fallback;
    }

    if (!int.TryParse(raw, out var parsed) || parsed < min || parsed > max)
    {
        throw new InvalidOperationException(
            $"Environment variable '{name}' must be an integer between {min} and {max}.");
    }

    return parsed;
}

internal sealed class GmailAssistantService
{
    private readonly string? _clientSecretPath;
    private readonly string _tokenStorePath;
    private readonly string? _expectedAccount;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private GmailService? _gmail;

    private GmailAssistantService(string? clientSecretPath, string tokenStorePath, string? expectedAccount)
    {
        _clientSecretPath = clientSecretPath;
        _tokenStorePath = tokenStorePath;
        _expectedAccount = expectedAccount;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_clientSecretPath);

    public static GmailAssistantService FromEnvironment()
    {
        var credentialsPath = Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET_PATH");
        var tokenStorePath = Environment.GetEnvironmentVariable("GMAIL_TOKEN_STORE_PATH");
        var expectedAccount = Environment.GetEnvironmentVariable("GMAIL_EXPECTED_ACCOUNT_EMAIL");

        if (!string.IsNullOrWhiteSpace(credentialsPath))
        {
            credentialsPath = ResolvePath(credentialsPath);
        }

        tokenStorePath = string.IsNullOrWhiteSpace(tokenStorePath)
            ? ResolvePath(".gmail-token-store")
            : ResolvePath(tokenStorePath);

        return new GmailAssistantService(credentialsPath, tokenStorePath, expectedAccount);
    }

    public string GetSetupStatusText()
    {
        if (IsConfigured)
        {
            return $"Gmail integration is configured.\nClient secret path: {_clientSecretPath}\nToken cache path: {_tokenStorePath}";
        }

        return "Gmail integration is not configured yet.\nSet GMAIL_CLIENT_SECRET_PATH to your Google OAuth client-secret JSON file, then restart the app.";
    }

    public object GetSetupStatus(string? expectedAccount = null)
    {
        var target = string.IsNullOrWhiteSpace(expectedAccount) ? _expectedAccount : expectedAccount;
        return new
        {
            configured = IsConfigured,
            expectedAccount = target,
            clientSecretPath = _clientSecretPath,
            tokenStorePath = _tokenStorePath,
            setup = new[]
            {
                "Create a Google Cloud project and enable Gmail API.",
                "Create OAuth client credentials (Desktop app).",
                "Set GMAIL_CLIENT_SECRET_PATH to the downloaded credentials JSON file.",
                "Run the app and ask for unread emails; browser-based consent will complete the first login.",
                "Keep scope minimal (gmail.readonly) for safety."
            }
        };
    }

    public async Task<object> ListUnreadMessagesAsync(int maxResults, string? query)
    {
        if (!IsConfigured)
        {
            return new
            {
                error = "Gmail is not configured.",
                status = GetSetupStatus()
            };
        }

        var service = await GetServiceAsync();
        var limit = Math.Clamp(maxResults, 1, 20);

        var listRequest = service.Users.Messages.List("me");
        listRequest.Q = string.IsNullOrWhiteSpace(query) ? "in:inbox is:unread" : $"in:inbox is:unread {query}";
        listRequest.MaxResults = limit;

        var listResponse = await listRequest.ExecuteAsync();
        var messages = listResponse.Messages ?? [];

        var items = new List<object>(messages.Count);
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Id))
            {
                continue;
            }

            var getRequest = service.Users.Messages.Get("me", message.Id);
            getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;

            var detail = await getRequest.ExecuteAsync();
            var headers = ToHeaderMap(detail.Payload?.Headers);
            items.Add(new
            {
                messageId = detail.Id,
                from = headers.TryGetValue("From", out var from) ? from : "(unknown sender)",
                subject = headers.TryGetValue("Subject", out var subject) ? subject : "(no subject)",
                date = headers.TryGetValue("Date", out var date) ? date : "(unknown date)",
                snippet = detail.Snippet
            });
        }

        return new
        {
            account = _expectedAccount ?? "me",
            unreadReturned = items.Count,
            messages = items
        };
    }

    public async Task<object> ReadMessageAsync(string messageId)
    {
        if (!IsConfigured)
        {
            return new
            {
                error = "Gmail is not configured.",
                status = GetSetupStatus()
            };
        }

        if (string.IsNullOrWhiteSpace(messageId))
        {
            return new { error = "messageId is required." };
        }

        var service = await GetServiceAsync();
        var getRequest = service.Users.Messages.Get("me", messageId);
        getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;

        var detail = await getRequest.ExecuteAsync();
        var headers = ToHeaderMap(detail.Payload?.Headers);
        var bodyPreview = ExtractMessageBody(detail.Payload);
        if (bodyPreview.Length > 4000)
        {
            bodyPreview = bodyPreview[..4000];
        }

        return new
        {
            messageId = detail.Id,
            threadId = detail.ThreadId,
            from = headers.TryGetValue("From", out var from) ? from : "(unknown sender)",
            to = headers.TryGetValue("To", out var to) ? to : "(unknown recipient)",
            subject = headers.TryGetValue("Subject", out var subject) ? subject : "(no subject)",
            date = headers.TryGetValue("Date", out var date) ? date : "(unknown date)",
            snippet = detail.Snippet,
            bodyPreview
        };
    }

    private async Task<GmailService> GetServiceAsync()
    {
        if (_gmail is not null)
        {
            return _gmail;
        }

        await _initLock.WaitAsync();
        try
        {
            if (_gmail is not null)
            {
                return _gmail;
            }

            if (string.IsNullOrWhiteSpace(_clientSecretPath) || !File.Exists(_clientSecretPath))
            {
                throw new InvalidOperationException(
                    $"GMAIL_CLIENT_SECRET_PATH is missing or points to a file that does not exist: '{_clientSecretPath}'.");
            }

            await using var stream = new FileStream(_clientSecretPath, FileMode.Open, FileAccess.Read);
            var secrets = GoogleClientSecrets.FromStream(stream).Secrets;

            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                [GmailService.Scope.GmailReadonly],
                _expectedAccount ?? "default-user",
                CancellationToken.None,
                new FileDataStore(_tokenStorePath, true));

            _gmail = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "PersonalAssistant.TelegramCopilot"
            });

            return _gmail;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static Dictionary<string, string> ToHeaderMap(IList<MessagePartHeader>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result;
        }

        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                continue;
            }

            result[header.Name] = header.Value ?? string.Empty;
        }

        return result;
    }

    private static string ExtractMessageBody(MessagePart? payload)
    {
        if (payload is null)
        {
            return string.Empty;
        }

        var plainText = ExtractBodyByMimeType(payload, "text/plain");
        if (!string.IsNullOrWhiteSpace(plainText))
        {
            return plainText;
        }

        var htmlText = ExtractBodyByMimeType(payload, "text/html");
        return string.IsNullOrWhiteSpace(htmlText) ? string.Empty : StripHtmlTags(htmlText);
    }

    private static string ExtractBodyByMimeType(MessagePart part, string mimeType)
    {
        if (string.Equals(part.MimeType, mimeType, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(part.Body?.Data))
        {
            return DecodeBase64Url(part.Body.Data);
        }

        if (part.Parts is null)
        {
            return string.Empty;
        }

        foreach (var child in part.Parts)
        {
            var extracted = ExtractBodyByMimeType(child, mimeType);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }
        }

        return string.Empty;
    }

    private static string DecodeBase64Url(string input)
    {
        var normalized = input.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        var bytes = Convert.FromBase64String(normalized);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static string StripHtmlTags(string html)
    {
        var output = html
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", "\n", StringComparison.OrdinalIgnoreCase);

        var withoutTags = System.Text.RegularExpressions.Regex.Replace(output, "<.*?>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path, Environment.CurrentDirectory);
    }
}

internal sealed class TelegramApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public TelegramApiClient(string botToken)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{botToken}/")
        };
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long? offset,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("timeout", timeoutSeconds.ToString()),
            new("allowed_updates", "[\"message\"]")
        };

        if (offset.HasValue)
        {
            parameters.Add(new("offset", offset.Value.ToString()));
        }

        using var response = await _httpClient.PostAsync("getUpdates", new FormUrlEncodedContent(parameters), cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<TelegramApiResponse<List<TelegramUpdate>>>(cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode || payload is null || !payload.Ok || payload.Result is null)
        {
            var description = payload?.Description ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Telegram getUpdates failed: {description}");
        }

        return payload.Result;
    }

    public async Task SendMessageInChunksAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        foreach (var chunk in ChunkText(text, 4000))
        {
            await SendMessageAsync(chatId, chunk, cancellationToken);
        }
    }

    private async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var payload = new List<KeyValuePair<string, string>>
        {
            new("chat_id", chatId.ToString()),
            new("text", text),
            new("disable_web_page_preview", "true")
        };

        using var response = await _httpClient.PostAsync("sendMessage", new FormUrlEncodedContent(payload), cancellationToken);
        var parsed = await response.Content.ReadFromJsonAsync<TelegramApiResponse<JsonElement>>(cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode || parsed is null || !parsed.Ok)
        {
            var description = parsed?.Description ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Telegram sendMessage failed: {description}");
        }
    }

    private static IEnumerable<string> ChunkText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(maxLength, text.Length - start);
            yield return text.Substring(start, length);
            start += length;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed class TelegramApiResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public T? Result { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

internal sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; init; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; init; }
}

internal sealed class TelegramMessage
{
    [JsonPropertyName("chat")]
    public TelegramChat Chat { get; init; } = new();

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

internal sealed class TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}
