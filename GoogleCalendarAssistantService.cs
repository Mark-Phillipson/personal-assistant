using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

public sealed class GoogleCalendarAssistantService
{
    private CalendarService? _calendar;
    private readonly string? _clientSecretPath;
    private readonly string _tokenStorePath;
    private readonly string? _expectedAccount;
    private static readonly ConcurrentDictionary<string, (DateTime Expiry, IList<Event> Events)> _cache = new();

    /// <summary>
    /// Optional callback invoked when device-code authorization is required.
    /// Receives (verificationUrl, message), where <c>message</c> is a preformatted
    /// prompt ready to display to the user. Use this to route the authorization
    /// instructions to the channel that triggered the request (for example, Telegram)
    /// instead of writing them to the console.
    /// </summary>
    public Func<string, string, Task>? AuthorizationNotifier { get; set; }

    private GoogleCalendarAssistantService(string? clientSecretPath, string tokenStorePath, string? expectedAccount)
    {
        _clientSecretPath = clientSecretPath;
        _tokenStorePath = tokenStorePath;
        _expectedAccount = expectedAccount;
    }

    public static GoogleCalendarAssistantService FromEnvironment()
    {
        var credentialsPath = Environment.GetEnvironmentVariable("CALENDAR_CLIENT_SECRET_PATH");
        var tokenStorePath = Environment.GetEnvironmentVariable("CALENDAR_TOKEN_STORE_PATH") ?? ".calendar-token-store";
        var expectedAccount = Environment.GetEnvironmentVariable("CALENDAR_EXPECTED_ACCOUNT_EMAIL");
        return new GoogleCalendarAssistantService(credentialsPath, tokenStorePath, expectedAccount);
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_clientSecretPath) && File.Exists(_clientSecretPath);

    public string GetSetupStatusText()
    {
        if (!IsConfigured)
        {
            return "Google Calendar integration is not configured yet.\n" +
                   "Set CALENDAR_CLIENT_SECRET_PATH to your Google OAuth client-secret JSON file, then restart the app.\n" +
                   "Alternatively, set CALENDAR_CLIENT_SECRET_PATH to a valid client secret and run the app to authenticate via the device-code flow.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Google Calendar integration is configured.");
        sb.AppendLine($"Client secret path: {_clientSecretPath}");
        sb.AppendLine($"Token cache path: {_tokenStorePath}");

        if (Directory.Exists(_tokenStorePath) && Directory.EnumerateFiles(_tokenStorePath).Any(p => Path.GetFileName(p).Contains("TokenResponse")))
        {
            sb.AppendLine("An OAuth token appears to be cached; the assistant can access Calendar.");
        }
        else
        {
            sb.AppendLine("No cached OAuth token found.");
            sb.AppendLine("Run the app to trigger an authentication flow. The app supports both browser-based and device-code flows (it will display a URL and code to enter).");
        }

        return sb.ToString();
    }

    private async Task<CalendarService> GetServiceAsync()
    {
        if (_calendar is not null)
            return _calendar;
        if (!IsConfigured)
            throw new InvalidOperationException("Google Calendar is not configured.");

        using var stream = new FileStream(_clientSecretPath!, FileMode.Open, FileAccess.Read);
        var clientSecrets = GoogleClientSecrets.FromStream(stream).Secrets;
        var scopes = new[] { CalendarService.Scope.Calendar };

        static string? NormalizeEmail(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

        static string? TryGetEmailFromIdToken(string? idToken)
        {
            if (string.IsNullOrWhiteSpace(idToken))
                return null;

            try
            {
                var parts = idToken.Split('.');
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
                    return null;

                var payload = parts[1]
                    .Replace('-', '+')
                    .Replace('_', '/');

                switch (payload.Length % 4)
                {
                    case 2:
                        payload += "==";
                        break;
                    case 3:
                        payload += "=";
                        break;
                }

                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("email", out var emailElement)
                    ? NormalizeEmail(emailElement.GetString())
                    : null;
            }
            catch
            {
                return null;
            }
        }

        static async Task<Google.Apis.Auth.OAuth2.Responses.TokenResponse> ReadTokenResponseAsync(string tokenFile)
        {
            var tokenJson = await File.ReadAllTextAsync(tokenFile);
            var token = new Google.Apis.Auth.OAuth2.Responses.TokenResponse();
            using var doc = JsonDocument.Parse(tokenJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("access_token", out var at)) token.AccessToken = at.GetString();
            if (root.TryGetProperty("refresh_token", out var rt)) token.RefreshToken = rt.GetString();
            if (root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var eiVal)) token.ExpiresInSeconds = eiVal;
            if (root.TryGetProperty("scope", out var sc)) token.Scope = sc.GetString();
            if (root.TryGetProperty("token_type", out var tt)) token.TokenType = tt.GetString();
            if (root.TryGetProperty("id_token", out var idt)) token.IdToken = idt.GetString();
            return token;
        }

        var expectedAccountEmail = NormalizeEmail(Environment.GetEnvironmentVariable("CALENDAR_EXPECTED_ACCOUNT_EMAIL"));

        // Try to reuse a cached token file (if present)
        try
        {
            if (Directory.Exists(_tokenStorePath))
            {
                const string tokenFilePrefix = "Google.Apis.Auth.OAuth2.Responses.TokenResponse-";
                var tokenFiles = Directory.EnumerateFiles(_tokenStorePath)
                    .Where(f => Path.GetFileName(f).StartsWith(tokenFilePrefix, StringComparison.Ordinal))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList();

                if (tokenFiles.Count > 0)
                {
                    var tokenCandidates = new List<(string FilePath, string UserId, string? AccountEmail, Google.Apis.Auth.OAuth2.Responses.TokenResponse Token)>();
                    foreach (var candidateFile in tokenFiles)
                    {
                        var token = await ReadTokenResponseAsync(candidateFile);
                        var userId = Path.GetFileName(candidateFile).Substring(tokenFilePrefix.Length);
                        var accountEmail = TryGetEmailFromIdToken(token.IdToken) ?? NormalizeEmail(userId);
                        tokenCandidates.Add((candidateFile, userId, accountEmail, token));
                    }

                    (string FilePath, string UserId, string? AccountEmail, Google.Apis.Auth.OAuth2.Responses.TokenResponse Token)? selectedCandidate = null;

                    if (!string.IsNullOrEmpty(expectedAccountEmail))
                    {
                        var matches = tokenCandidates
                            .Where(c => string.Equals(c.AccountEmail, expectedAccountEmail, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(NormalizeEmail(c.UserId), expectedAccountEmail, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (matches.Count == 1)
                        {
                            selectedCandidate = matches[0];
                        }
                        else if (matches.Count > 1)
                        {
                            throw new InvalidOperationException($"Multiple cached Google Calendar tokens matched CALENDAR_EXPECTED_ACCOUNT_EMAIL '{expectedAccountEmail}'. Clear old cached tokens in '{_tokenStorePath}' and re-authenticate.");
                        }
                    }
                    else if (tokenCandidates.Count == 1)
                    {
                        selectedCandidate = tokenCandidates[0];
                    }
                    else
                    {
                        throw new InvalidOperationException($"Multiple cached Google Calendar tokens were found in '{_tokenStorePath}'. Set CALENDAR_EXPECTED_ACCOUNT_EMAIL to the intended Google account email or clear old cached tokens before continuing.");
                    }

                    if (selectedCandidate is not null)
                    {
                        var dataStore = new FileDataStore(_tokenStorePath, true);
                        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
                        {
                            ClientSecrets = clientSecrets,
                            Scopes = scopes,
                            DataStore = dataStore
                        });

                        var credential = new UserCredential(flow, selectedCandidate.Value.UserId, selectedCandidate.Value.Token);

                        // Attempt to refresh if we have a refresh token
                        if (!string.IsNullOrEmpty(credential.Token?.RefreshToken))
                        {
                            try { await credential.RefreshTokenAsync(CancellationToken.None); }
                            catch { /* ignore refresh errors and fall back to interactive/device flow */ }
                        }

                        _calendar = new CalendarService(new BaseClientService.Initializer
                        {
                            HttpClientInitializer = credential,
                            ApplicationName = "Personal Assistant Calendar Integration"
                        });

                        return _calendar;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // non-fatal: continue to interactive/device flow and surface a helpful message
            Console.WriteLine($"Warning reading cached token: {ex.Message}");
        }

        // No cached token found: attempt device-code flow (fallback). If the device endpoint fails, fall back to browser flow.
        using var httpClient = new HttpClient();
        var deviceRequest = new Dictionary<string, string>
        {
            { "client_id", clientSecrets.ClientId },
            { "scope", string.Join(' ', scopes) }
        };
        if (!string.IsNullOrEmpty(clientSecrets.ClientSecret))
            deviceRequest["client_secret"] = clientSecrets.ClientSecret;

        var deviceResp = await httpClient.PostAsync("https://oauth2.googleapis.com/device/code", new FormUrlEncodedContent(deviceRequest));
        if (!deviceResp.IsSuccessStatusCode)
        {
            // As a fallback use the existing browser-based helper which will open a browser for an installed app flow
            var cred = await GoogleWebAuthorizationBroker.AuthorizeAsync(clientSecrets, scopes, _expectedAccount ?? "user", CancellationToken.None, new FileDataStore(_tokenStorePath, true));
            _calendar = new CalendarService(new BaseClientService.Initializer { HttpClientInitializer = cred, ApplicationName = "Personal Assistant Calendar Integration" });
            return _calendar;
        }

        var deviceJson = await deviceResp.Content.ReadAsStringAsync();
        using var deviceDoc = JsonDocument.Parse(deviceJson);
        var rootDevice = deviceDoc.RootElement;
        var deviceCode = rootDevice.GetProperty("device_code").GetString();
        var userCode = rootDevice.GetProperty("user_code").GetString();
        var verificationUrl = rootDevice.TryGetProperty("verification_url", out var vv) ? vv.GetString() : rootDevice.GetProperty("verification_uri").GetString();
        var expiresIn = rootDevice.GetProperty("expires_in").GetInt32();
        var interval = rootDevice.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;

        // Surface clear instructions to the operator so they can finish auth in a browser on any device.
        Console.WriteLine();
        Console.WriteLine("Google Calendar authorization required.");
        Console.WriteLine($"Open: {verificationUrl}");
        Console.WriteLine($"Enter code: {userCode}");
        Console.WriteLine("Waiting for authorization... (this process will poll the token endpoint)");

        if (AuthorizationNotifier is not null)
        {
            var encodedVerificationUrl = System.Net.WebUtility.HtmlEncode(verificationUrl ?? string.Empty);
            var encodedUserCode = System.Net.WebUtility.HtmlEncode(userCode ?? string.Empty);
            var msg = $"🔐 <b>Google Calendar authorization required</b>\n\n" +
                      $"1. Open: {encodedVerificationUrl}\n" +
                      $"2. Enter code: <code>{encodedUserCode}</code>\n\n" +
                      $"I'll keep polling and will confirm once you've granted access.";
            await AuthorizationNotifier(verificationUrl ?? string.Empty, msg);
        }

        var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval * 1000);
            var tokenRequest = new Dictionary<string, string>
            {
                { "client_id", clientSecrets.ClientId },
                { "device_code", deviceCode! },
                { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" }
            };
            if (!string.IsNullOrEmpty(clientSecrets.ClientSecret))
                tokenRequest["client_secret"] = clientSecrets.ClientSecret;

            var tokenResp = await httpClient.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(tokenRequest));
            var tokenJson = await tokenResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(tokenJson);
            var root = doc.RootElement;

            if (!tokenResp.IsSuccessStatusCode)
            {
                if (root.TryGetProperty("error", out var err))
                {
                    var errStr = err.GetString();
                    if (errStr == "authorization_pending")
                    {
                        continue;
                    }
                    if (errStr == "slow_down")
                    {
                        interval += 5;
                        continue;
                    }
                    throw new InvalidOperationException($"Device authorization failed: {errStr}");
                }

                continue;
            }

            var token = new Google.Apis.Auth.OAuth2.Responses.TokenResponse();
            if (root.TryGetProperty("access_token", out var at2)) token.AccessToken = at2.GetString();
            if (root.TryGetProperty("refresh_token", out var rt2)) token.RefreshToken = rt2.GetString();
            if (root.TryGetProperty("expires_in", out var ei2) && ei2.TryGetInt32(out var eiVal2)) token.ExpiresInSeconds = eiVal2;
            if (root.TryGetProperty("scope", out var sc2)) token.Scope = sc2.GetString();
            if (root.TryGetProperty("token_type", out var tt2)) token.TokenType = tt2.GetString();
            if (root.TryGetProperty("id_token", out var idt2)) token.IdToken = idt2.GetString();

            // Store token using the same key format the FileDataStore expects so subsequent runs reuse it.
            var userId = _expectedAccount ?? "user";
            var dataStore = new FileDataStore(_tokenStorePath, true);
            await dataStore.StoreAsync($"Google.Apis.Auth.OAuth2.Responses.TokenResponse-{userId}", token);

            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = clientSecrets,
                Scopes = scopes,
                DataStore = dataStore
            });

            var credential = new UserCredential(flow, userId, token);

            _calendar = new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Personal Assistant Calendar Integration"
            });

            if (AuthorizationNotifier is not null)
                await AuthorizationNotifier(string.Empty, "✅ Google Calendar authorization successful! Now completing your request...");

            return _calendar;
        }

        throw new InvalidOperationException("Device authorization timed out or was not completed.");
    }

    public async Task<IList<Event>> ListUpcomingEventsAsync(int maxResults = 5)
    {
        var cacheSeconds = EnvironmentSettings.ReadInt("CALENDAR_CACHE_SECONDS", fallback: 30, min: 1, max: 3600);
        var cacheKey = $"events:{_expectedAccount ?? "user"}:{maxResults}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            return cached.Events;
        }

        var service = await GetServiceAsync();

        var selectedCalendars = await ListSelectedCalendarsAsync(service);
        var collected = new List<Event>();

        var requestTimeoutSeconds = EnvironmentSettings.ReadInt("CALENDAR_REQUEST_TIMEOUT_SECONDS", fallback: 10, min: 1, max: 120);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var tasks = selectedCalendars.Select(async calendar =>
        {
            try
            {
                var request = service.Events.List(calendar.Id);
                request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow;
                request.ShowDeleted = false;
                request.SingleEvents = true;
                request.MaxResults = Math.Clamp(maxResults, 1, 20);
                request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

                var execTask = request.ExecuteAsync();
                var completed = await Task.WhenAny(execTask, Task.Delay(TimeSpan.FromSeconds(requestTimeoutSeconds)));
                if (completed != execTask)
                {
                    Console.WriteLine($"[calendar] request for '{calendar.Id}' timed out after {requestTimeoutSeconds}s");
                    return Enumerable.Empty<Event>();
                }

                var events = await execTask;
                if (events.Items is null)
                {
                    return Enumerable.Empty<Event>();
                }

                foreach (var calendarEvent in events.Items)
                {
                    if (!calendar.IsPrimary)
                    {
                        var summary = string.IsNullOrWhiteSpace(calendarEvent.Summary) ? "(No title)" : calendarEvent.Summary;
                        calendarEvent.Summary = $"[{calendar.Name}] {summary}";
                    }
                }

                return events.Items.AsEnumerable();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[calendar] error fetching events for '{calendar.Id}': {ex.Message}");
                return Enumerable.Empty<Event>();
            }
        });

        var results = await Task.WhenAll(tasks);
        foreach (var r in results)
        {
            collected.AddRange(r);
        }

        sw.Stop();
        Console.WriteLine($"[calendar] fetched events from {selectedCalendars.Count} calendars in {sw.ElapsedMilliseconds}ms");

        var finalList = collected
            .GroupBy(GetEventKey)
            .Select(group => group.First())
            .OrderBy(GetEventStart)
            .Take(Math.Clamp(maxResults, 1, 20))
            .ToList();

        _cache[cacheKey] = (DateTime.UtcNow.AddSeconds(cacheSeconds), finalList);

        return finalList;
    }

    private async Task<IList<(string Id, string Name, bool IsPrimary)>> ListSelectedCalendarsAsync(CalendarService service)
    {
        var calendars = new List<(string Id, string Name, bool IsPrimary)>();
        string? pageToken = null;

        do
        {
            var request = service.CalendarList.List();
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync();

            if (response.Items is not null)
            {
                foreach (var entry in response.Items)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id))
                    {
                        continue;
                    }

                    var isPrimary = entry.Primary == true;
                    var isSelected = entry.Selected == true;
                    if (!isPrimary && !isSelected)
                    {
                        continue;
                    }

                    var name = string.IsNullOrWhiteSpace(entry.Summary) ? entry.Id : entry.Summary;
                    calendars.Add((entry.Id, name, isPrimary));
                }
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        if (calendars.Count == 0)
        {
            calendars.Add(("primary", "Primary", true));
        }

        return calendars;
    }

    private static DateTimeOffset GetEventStart(Event calendarEvent)
    {
        if (calendarEvent.Start?.DateTimeDateTimeOffset is DateTimeOffset dateTime)
        {
            return dateTime;
        }

        if (DateTime.TryParse(calendarEvent.Start?.Date, out var allDayDate))
        {
            return new DateTimeOffset(allDayDate, TimeSpan.Zero);
        }

        return DateTimeOffset.MaxValue;
    }

    private static string GetEventKey(Event calendarEvent)
    {
        var id = calendarEvent.Id ?? string.Empty;
        var iCalUid = calendarEvent.ICalUID ?? string.Empty;
        var startDateTime = calendarEvent.Start?.DateTimeDateTimeOffset?.UtcDateTime.ToString("O") ?? string.Empty;
        var startDate = calendarEvent.Start?.Date ?? string.Empty;
        var summary = calendarEvent.Summary ?? string.Empty;
        return $"{id}|{iCalUid}|{startDateTime}|{startDate}|{summary}";
    }

    public async Task<Event> CreateEventAsync(string summary, string description, DateTime start, DateTime end)
    {
        var service = await GetServiceAsync();

        // Treat unspecified DateTime as local time so offsets are correct
        if (start.Kind == DateTimeKind.Unspecified)
            start = DateTime.SpecifyKind(start, DateTimeKind.Local);
        if (end.Kind == DateTimeKind.Unspecified)
            end = DateTime.SpecifyKind(end, DateTimeKind.Local);

        var startOffset = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
        var endOffset = new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end));
        var timeZoneId = TimeZoneInfo.Local.Id;

        var newEvent = new Event
        {
            Summary = summary,
            Description = description,
            Start = new EventDateTime { DateTimeDateTimeOffset = startOffset, TimeZone = timeZoneId },
            End = new EventDateTime { DateTimeDateTimeOffset = endOffset, TimeZone = timeZoneId }
        };
        var request = service.Events.Insert(newEvent, "primary");
        return await request.ExecuteAsync();
    }
}
