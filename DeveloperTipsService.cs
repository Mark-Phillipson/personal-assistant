using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Linq;

internal sealed class DeveloperTipsService
{
    private readonly TextToSpeechService _tts;
    private readonly TelegramApiClient _telegram;
    private readonly string _tipsFilePath;
    private readonly string _stateFilePath;
    private readonly object _sync = new();
    private DevTipsState _state = new DevTipsState();
    private List<Tip> _tips = new();
    private readonly Random _rng = new();

    public DeveloperTipsService(TextToSpeechService tts, TelegramApiClient telegram, string? tipsFilePath = null, string? stateFilePath = null)
    {
        _tts = tts;
        _telegram = telegram;
        _tipsFilePath = tipsFilePath ?? Path.Combine(AppContext.BaseDirectory, "documents", "developer-tips.json");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var defaultState = Path.Combine(appData, "personal-assistant", "dev-tips-state.json");
        _stateFilePath = stateFilePath ?? EnvironmentSettings.ReadString("DEV_TIPS_STATE_PATH", defaultState);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        await LoadTipsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadTipsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tipsPath = _tipsFilePath;

            if (!File.Exists(tipsPath))
            {
                // Try a few fallback locations so the feature works when running from the repo root
                var candidates = new List<string>
                {
                    Path.Combine(Directory.GetCurrentDirectory(), "documents", "developer-tips.json"),
                    Path.Combine(AppContext.BaseDirectory, "documents", "developer-tips.json")
                };

                // Walk up from the current directory looking for a documents folder
                string? probe = Directory.GetCurrentDirectory();
                for (var i = 0; i < 6 && probe is not null; i++)
                {
                    candidates.Add(Path.Combine(probe, "documents", "developer-tips.json"));
                    var parent = Directory.GetParent(probe);
                    probe = parent?.FullName;
                }

                // Walk up from the AppContext.BaseDirectory too
                probe = AppContext.BaseDirectory;
                for (var i = 0; i < 6 && probe is not null; i++)
                {
                    candidates.Add(Path.Combine(probe, "documents", "developer-tips.json"));
                    var parent = Directory.GetParent(probe);
                    probe = parent?.FullName;
                }

                var found = candidates.FirstOrDefault(File.Exists);
                if (found is null)
                {
                    // no-op: caller may add a tips file later; keep empty list
                    _tips = new List<Tip>();
                    Console.Error.WriteLine("[devtips] tips file not found; looked in multiple locations.");
                    return;
                }

                tipsPath = found;
            }

            var json = await File.ReadAllTextAsync(tipsPath, cancellationToken).ConfigureAwait(false);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var items = JsonSerializer.Deserialize<List<Tip>>(json, opts);
            _tips = items ?? new List<Tip>();
        }
        catch
        {
            _tips = new List<Tip>();
        }
    }

    private async Task LoadStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (!File.Exists(_stateFilePath))
            {
                _state = new DevTipsState { Enabled = true, Subscribers = new List<SubscriberEntry>(), FrequencyMinutes = 60 };
                await SaveStateAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var json = await File.ReadAllTextAsync(_stateFilePath, cancellationToken).ConfigureAwait(false);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var loaded = JsonSerializer.Deserialize<DevTipsState>(json, opts);
            if (loaded is not null) _state = loaded;
        }
        catch
        {
            _state = new DevTipsState { Enabled = true, Subscribers = new List<SubscriberEntry>(), FrequencyMinutes = 60 };
        }
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _stateFilePath + ".tmp";
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_state, opts);
            await File.WriteAllTextAsync(tmp, json, cancellationToken).ConfigureAwait(false);
            File.Copy(tmp, _stateFilePath, overwrite: true);
            try { File.Delete(tmp); } catch { }
        }
        catch
        {
            // ignore persistence errors for now
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delay = ComputeDelayUntilNext(cancellationToken);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                await AnnounceAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[devtips.loop.error] {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeDelayUntilNext(CancellationToken cancellationToken)
    {
        // Determine scheduling mode
        var mode = _state?.ScheduleMode?.ToLowerInvariant();
        var frequency = (_state?.FrequencyMinutes).GetValueOrDefault(60);
        if (frequency <= 0) frequency = 60;

        // Hourly anchored behaviour (default)
        if (string.IsNullOrWhiteSpace(mode) || mode == "hourly")
        {
            var now = DateTime.Now;
            var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1);
            var delay = next - now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);
            return delay;
        }

        // Fixed interval (minutes)
        if (mode == "fixed")
        {
            return TimeSpan.FromMinutes(frequency);
        }

        // Specific times of day
        if (mode == "times")
        {
            try
            {
                var now = DateTime.Now;
                var times = (_state?.TimesOfDay ?? new List<string>())
                    .Select(s =>
                    {
                        if (TimeSpan.TryParseExact(s, "hh\\:mm", CultureInfo.InvariantCulture, out var ts)) return ts;
                        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out ts)) return ts;
                        return (TimeSpan?)null;
                    })
                    .Where(ts => ts.HasValue)
                    .Select(ts => ts!.Value)
                    .ToList();

                if (!times.Any()) return TimeSpan.FromMinutes(1);

                var nextDates = times.Select(t => new DateTime(now.Year, now.Month, now.Day).Add(t))
                    .Select(dt => dt <= now ? dt.AddDays(1) : dt)
                    .OrderBy(dt => dt)
                    .ToList();

                var next = nextDates.First();
                var delay = next - now;
                if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);
                return delay;
            }
            catch
            {
                return TimeSpan.FromMinutes(1);
            }
        }

        // Random interval between min/max minutes
        if (mode == "random")
        {
            var min = _state?.RandomMinMinutes.GetValueOrDefault(15) ?? 15;
            var max = _state?.RandomMaxMinutes.GetValueOrDefault(60) ?? 60;
            if (min <= 0) min = 1;
            if (max < min) max = min;
            var minutes = _rng.Next(min, max + 1);
            return TimeSpan.FromMinutes(minutes);
        }

        // Fallback to fixed frequency
        return TimeSpan.FromMinutes(frequency);
    }

        private async Task AnnounceAllAsync(CancellationToken cancellationToken)
    {
        var lastTipId = _state.LastTipId;
        // For top-of-hour announcements prefer tips from any category (prefer non-general tags)
        Tip? localTip = PickRandomTipForTopOfHour(lastTipId);
        if (localTip is not null)
        {
            try
            {
                // Random pause between 500ms and 1200ms for top-of-hour announcement
                var pauseMs = _rng.Next(500, 1201);
                var ssml = BuildAnnouncementSsml(localTip.Text, pauseMs);
                await _tts.TrySpeakPreviewAsync(ssml, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[devtips.tts.error] {ex.Message}");
            }
        }

        List<SubscriberEntry> subs;
        lock (_sync)
        {
            subs = _state.Subscribers?.ToList() ?? new List<SubscriberEntry>();
            _state.LastAnnouncedUtc = DateTime.UtcNow;
        }

        string? batchSelectedTipId = localTip?.Id;

        foreach (var sub in subs)
        {
            try
            {
            var tip = PickRandomTipForCategory(sub.Category ?? "general", lastTipId) ?? localTip;
                if (tip is null) continue;

                var message = $"Developer tip:\n\n{tip.Text}";

                if (sub.SendAudio)
                {
                    var ssmlForSubscriber = BuildAnnouncementSsml(tip.Text);
                    var wav = await _tts.SynthesizePreviewToWavFileAsync(ssmlForSubscriber, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(wav) && File.Exists(wav))
                    {
                        try
                        {
                            await _telegram.SendDocumentAsync(sub.ChatId, wav, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[devtips.telegram.doc.error] chat={sub.ChatId} {ex.Message}");
                            await _telegram.SendMessageInChunksAsync(sub.ChatId, message, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            try { File.Delete(wav); } catch { }
                        }
                    }
                    else
                    {
                        await _telegram.SendMessageInChunksAsync(sub.ChatId, message, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await _telegram.SendMessageInChunksAsync(sub.ChatId, message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[devtips.deliver.error] chat={sub.ChatId} {ex.Message}");
            }
        }

        // Persist the last tip id for next-announcement duplicate avoidance
        try
        {
            lock (_sync)
            {
                if (!string.IsNullOrWhiteSpace(batchSelectedTipId))
                {
                    _state.LastTipId = batchSelectedTipId;
                }
            }
        }
        catch { }

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private Tip? PickRandomTipForCategory(string category, string? excludeTipId = null)
    {
        if (_tips is null || _tips.Count == 0) return null;
        // Include tips that match the requested category OR are tagged as "general"
        var candidates = _tips
            .Where(t => t.Tags?.Any(tag => string.Equals(tag, category, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(tag, "general", StringComparison.OrdinalIgnoreCase)) ?? false)
            .ToList();

        if (!candidates.Any()) return null;

        if (!string.IsNullOrWhiteSpace(excludeTipId))
        {
            var filtered = candidates.Where(t => !string.Equals(t.Id, excludeTipId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Any()) candidates = filtered;
            // if filtering removes all candidates, fall back to original candidates so we can still return something
        }

        var idx = _rng.Next(candidates.Count);
        return candidates[idx];
    }

    private Tip? PickRandomTipForTopOfHour(string? excludeTipId = null)
    {
        if (_tips is null || _tips.Count == 0) return null;

        // Prefer tips that include tags other than 'general' so top-of-hour feels varied.
        var nonGeneral = _tips.Where(t => (t.Tags?.Any(tag => !string.Equals(tag, "general", StringComparison.OrdinalIgnoreCase)) ?? false)).ToList();
        var candidates = nonGeneral.Any() ? nonGeneral : _tips.ToList();

        if (!string.IsNullOrWhiteSpace(excludeTipId))
        {
            var filtered = candidates.Where(t => !string.Equals(t.Id, excludeTipId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Any()) candidates = filtered;
        }

        var idx = _rng.Next(candidates.Count);
        return candidates[idx];
    }

    public async Task EnableForChatAsync(long chatId, string category = "general", bool sendAudio = false, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _state ??= new DevTipsState { Enabled = true, FrequencyMinutes = 60, Subscribers = new List<SubscriberEntry>() };
            var exists = _state.Subscribers?.FirstOrDefault(s => s.ChatId == chatId);
            if (exists is null)
            {
                _state.Subscribers.Add(new SubscriberEntry { ChatId = chatId, Category = category, SendAudio = sendAudio });
            }
            else
            {
                exists.Category = category;
                exists.SendAudio = sendAudio;
            }
        }

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DisableForChatAsync(long chatId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _state.Subscribers?.RemoveAll(s => s.ChatId == chatId);
        }

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCategoryForChatAsync(long chatId, string category, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var sub = _state.Subscribers?.FirstOrDefault(s => s.ChatId == chatId);
            if (sub is not null) sub.Category = category;
            else _state.Subscribers.Add(new SubscriberEntry { ChatId = chatId, Category = category, SendAudio = false });
        }

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSendAudioForChatAsync(long chatId, bool sendAudio, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var sub = _state.Subscribers?.FirstOrDefault(s => s.ChatId == chatId);
            if (sub is not null) sub.SendAudio = sendAudio;
            else _state.Subscribers.Add(new SubscriberEntry { ChatId = chatId, Category = "general", SendAudio = sendAudio });
        }

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public string GetStatusForChat(long chatId)
    {
        lock (_sync)
        {
            var sub = _state.Subscribers?.FirstOrDefault(s => s.ChatId == chatId);
            if (sub is null) return "Not subscribed to developer tips.";
            return $"Subscribed: yes\nCategory: {sub.Category}\nSendAudio: {sub.SendAudio}";
        }
    }

    public async Task ForceAnnounceForChatAsync(long chatId, CancellationToken cancellationToken = default)
    {
        SubscriberEntry? sub;
        lock (_sync)
        {
            sub = _state.Subscribers?.FirstOrDefault(s => s.ChatId == chatId);
        }

        var category = sub?.Category ?? "general";
        // Ensure tips are loaded; try to reload if empty.
        if (_tips is null || _tips.Count == 0)
        {
            await LoadTipsAsync(cancellationToken).ConfigureAwait(false);
        }

        var tip = PickRandomTipForCategory(category, _state.LastTipId);
        if (tip is null)
        {
            await _telegram.SendMessageInChunksAsync(chatId, "No tips available.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var message = $"Developer tip:\n\n{tip.Text}";

        // Speak locally as well for immediate forced announcements (with time intro).
        try
        {
            var ssml = BuildAnnouncementSsml(tip.Text);
            await _tts.TrySpeakPreviewAsync(ssml, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[devtips.tts.error] force chat={chatId} {ex.Message}");
        }

        if (sub?.SendAudio ?? false)
        {
            var wav = await _tts.SynthesizePreviewToWavFileAsync(BuildAnnouncementSsml(tip.Text), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(wav) && File.Exists(wav))
            {
                try
                {
                    await _telegram.SendDocumentAsync(chatId, wav, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await _telegram.SendMessageInChunksAsync(chatId, message, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(wav); } catch { }
                }
            }
            else
            {
                await _telegram.SendMessageInChunksAsync(chatId, message, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await _telegram.SendMessageInChunksAsync(chatId, message, cancellationToken).ConfigureAwait(false);
        }

        // Persist last tip id so future announcements avoid repeating it
        try
        {
            lock (_sync)
            {
                _state.LastTipId = tip.Id;
            }
            await SaveStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { }
    }

    // Scheduling APIs
    public async Task SetScheduleHourlyAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _state.ScheduleMode = "hourly";
            _state.FrequencyMinutes = 60;
        }

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetScheduleFixedAsync(int minutes, CancellationToken cancellationToken = default)
    {
        if (minutes <= 0) minutes = 1;
        lock (_sync)
        {
            _state.ScheduleMode = "fixed";
            _state.FrequencyMinutes = minutes;
        }

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetScheduleTimesAsync(IEnumerable<TimeSpan> times, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _state.ScheduleMode = "times";
            _state.TimesOfDay = times.Select(ts => ts.ToString("hh\\:mm")).ToList();
        }

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetScheduleRandomAsync(int minMinutes, int maxMinutes, CancellationToken cancellationToken = default)
    {
        if (minMinutes <= 0) minMinutes = 1;
        if (maxMinutes < minMinutes) maxMinutes = minMinutes;

        lock (_sync)
        {
            _state.ScheduleMode = "random";
            _state.RandomMinMinutes = minMinutes;
            _state.RandomMaxMinutes = maxMinutes;
        }

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public string GetScheduleDescription()
    {
        lock (_sync)
        {
            var mode = _state.ScheduleMode ?? "hourly";
            switch (mode.ToLowerInvariant())
            {
                case "hourly":
                    return $"Schedule: hourly (top of the hour). FrequencyMinutes={_state.FrequencyMinutes}";
                case "fixed":
                    return $"Schedule: fixed interval every {_state.FrequencyMinutes} minute(s).";
                case "times":
                    var times = _state.TimesOfDay?.Count > 0 ? string.Join(", ", _state.TimesOfDay) : "none";
                    return $"Schedule: specific times of day: {times}.";
                case "random":
                    return $"Schedule: random between {_state.RandomMinMinutes} and {_state.RandomMaxMinutes} minutes.";
                default:
                    return $"Schedule: unknown mode '{mode}'.";
            }
        }
    }

    private static string EscapeForSsml(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private string BuildAnnouncementSsml(string tipText, int? pauseMs = null)
    {
        var time = DateTime.Now.ToString("h:mm tt", CultureInfo.CurrentCulture);
        var escapedTip = EscapeForSsml(tipText ?? string.Empty);
        var voice = EnvironmentSettings.ReadString("AZURE_SPEECH_VOICE", "en-GB-RyanNeural");
        var safeVoice = EscapeForSsml(voice);
        // Use provided pause (ms) or default to 700ms
        var pause = pauseMs ?? 700;
        var content = $"Time Is {EscapeForSsml(time)} Developer Tip Incoming.<break time=\"{pause}ms\"/> <prosody rate=\"-15%\">{escapedTip}</prosody>";
        var ssml = $"<speak version=\"1.0\" xml:lang=\"en-GB\"><voice name=\"{safeVoice}\">{content}</voice></speak>";
        return ssml;
    }

    private record Tip([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("text")] string Text, [property: JsonPropertyName("tags")] List<string> Tags);
    private class SubscriberEntry
    {
        [JsonPropertyName("chatId")] public long ChatId { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = "general";
        [JsonPropertyName("sendAudio")] public bool SendAudio { get; set; }
    }

    private class DevTipsState
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("subscribers")] public List<SubscriberEntry> Subscribers { get; set; } = new();
        [JsonPropertyName("lastAnnouncedUtc")] public DateTime? LastAnnouncedUtc { get; set; }
        [JsonPropertyName("frequencyMinutes")] public int FrequencyMinutes { get; set; } = 60;
        [JsonPropertyName("scheduleMode")] public string? ScheduleMode { get; set; }
        [JsonPropertyName("timesOfDay")] public List<string> TimesOfDay { get; set; } = new();
        [JsonPropertyName("randomMinMinutes")] public int? RandomMinMinutes { get; set; }
        [JsonPropertyName("randomMaxMinutes")] public int? RandomMaxMinutes { get; set; }
        [JsonPropertyName("lastTipId")] public string? LastTipId { get; set; }
    }
}
