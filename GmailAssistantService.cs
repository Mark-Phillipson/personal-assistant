using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

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

    public async Task<object> ListUnreadMessagesAsync(int maxResults, string? query, bool unreadOnly = true)
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

        var baseFilter = unreadOnly ? "in:inbox is:unread" : "in:inbox";
        var listRequest = service.Users.Messages.List("me");
        listRequest.Q = string.IsNullOrWhiteSpace(query) ? baseFilter : $"{baseFilter} {query}";
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

    public async Task<string> GetConsentUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(_clientSecretPath) || !File.Exists(_clientSecretPath))
        {
            throw new InvalidOperationException(
                $"GMAIL_CLIENT_SECRET_PATH is missing or points to a file that does not exist: '{_clientSecretPath}'.");
        }

        await using var stream = new FileStream(_clientSecretPath, FileMode.Open, FileAccess.Read);
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = new[] { GmailService.Scope.GmailReadonly }
        });

        var receiver = new LocalServerCodeReceiver();
        var authUrlObj = flow.CreateAuthorizationCodeRequest(receiver.RedirectUri);
        // Build() may return a Uri or string depending on library version; ensure a string return
        var built = authUrlObj.Build();
        var authUrl = built is Uri uri ? uri.AbsoluteUri : built.ToString();
        return authUrl;
    }

    /// <summary>
    /// Starts an interactive OAuth flow using a local server code receiver and stores credentials in the token store.
    /// This method will open the user's browser to complete the consent flow if needed.
    /// </summary>
    public async Task StartInteractiveAuthAsync()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Gmail is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_clientSecretPath) || !File.Exists(_clientSecretPath))
        {
            throw new InvalidOperationException(
                $"GMAIL_CLIENT_SECRET_PATH is missing or points to a file that does not exist: '{_clientSecretPath}'.");
        }

        await using var stream = new FileStream(_clientSecretPath, FileMode.Open, FileAccess.Read);
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;

        var receiver = new LocalServerCodeReceiver();

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            new[] { GmailService.Scope.GmailReadonly },
            _expectedAccount ?? "default-user",
            CancellationToken.None,
            new FileDataStore(_tokenStorePath, true),
            receiver);

        _gmail = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "PersonalAssistant.TelegramCopilot"
        });
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
