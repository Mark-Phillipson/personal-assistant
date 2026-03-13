using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public sealed class GoogleCalendarAssistantService
{
    private CalendarService? _calendar;
    private readonly string? _clientSecretPath;
    private readonly string _tokenStorePath;
    private readonly string? _expectedAccount;

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
        if (IsConfigured)
        {
            return $"Google Calendar integration is configured.\nClient secret path: {_clientSecretPath}\nToken cache path: {_tokenStorePath}";
        }
        return "Google Calendar integration is not configured yet.\nSet CALENDAR_CLIENT_SECRET_PATH to your Google OAuth client-secret JSON file, then restart the app.";
    }

    private async Task<CalendarService> GetServiceAsync()
    {
        if (_calendar is not null)
            return _calendar;
        if (!IsConfigured)
            throw new InvalidOperationException("Google Calendar is not configured.");
        using var stream = new FileStream(_clientSecretPath!, FileMode.Open, FileAccess.Read);
        var cred = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            new[] { CalendarService.Scope.Calendar },
            _expectedAccount ?? "user",
            CancellationToken.None,
            new FileDataStore(_tokenStorePath, true));
        _calendar = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = cred,
            ApplicationName = "Personal Assistant Calendar Integration"
        });
        return _calendar;
    }

    public async Task<IList<Event>> ListUpcomingEventsAsync(int maxResults = 5)
    {
        var service = await GetServiceAsync();
        var request = service.Events.List("primary");
        request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow;
        request.ShowDeleted = false;
        request.SingleEvents = true;
        request.MaxResults = maxResults;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
        var events = await request.ExecuteAsync();
        return events.Items ?? new List<Event>();
    }

    public async Task<Event> CreateEventAsync(string summary, string description, DateTime start, DateTime end)
    {
        var service = await GetServiceAsync();
        var newEvent = new Event
        {
            Summary = summary,
            Description = description,
            Start = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(start, TimeSpan.Zero), TimeZone = "UTC" },
            End = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(end, TimeSpan.Zero), TimeZone = "UTC" }
        };
        var request = service.Events.Insert(newEvent, "primary");
        return await request.ExecuteAsync();
    }
}
