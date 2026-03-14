using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        var selectedCalendars = await ListSelectedCalendarsAsync(service);
        var collected = new List<Event>();

        foreach (var calendar in selectedCalendars)
        {
            var request = service.Events.List(calendar.Id);
            request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow;
            request.ShowDeleted = false;
            request.SingleEvents = true;
            request.MaxResults = Math.Clamp(maxResults, 1, 20);
            request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

            var events = await request.ExecuteAsync();
            if (events.Items is null)
            {
                continue;
            }

            foreach (var calendarEvent in events.Items)
            {
                if (!calendar.IsPrimary)
                {
                    var summary = string.IsNullOrWhiteSpace(calendarEvent.Summary) ? "(No title)" : calendarEvent.Summary;
                    calendarEvent.Summary = $"[{calendar.Name}] {summary}";
                }

                collected.Add(calendarEvent);
            }
        }

        return collected
            .GroupBy(GetEventKey)
            .Select(group => group.First())
            .OrderBy(GetEventStart)
            .Take(Math.Clamp(maxResults, 1, 20))
            .ToList();
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
