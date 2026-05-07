using System.Text.Json;
using System.Text.Json.Serialization;

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
            if (!File.Exists(_tipsFilePath))
            {
                // no-op: caller may add a tips file later; keep empty list
                _tips = new List<Tip>();
                return;
            }

            var json = await File.ReadAllTextAsync(_tipsFilePath, cancellationToken).ConfigureAwait(false);
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
        var frequency = (_state?.FrequencyMinutes).GetValueOrDefault(60);
        if (frequency <= 0) frequency = 60;

        if (frequency == 60)
        {
            var now = DateTime.Now;
            var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1);
            var delay = next - now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);
            return delay;
        }

        return TimeSpan.FromMinutes(frequency);
    }

    private async Task AnnounceAllAsync(CancellationToken cancellationToken)
    {
        Tip? localTip = PickRandomTipForCategory("general");
        if (localTip is not null)
        {
            try
            {
                await _tts.TrySpeakPreviewAsync(localTip.Text, cancellationToken).ConfigureAwait(false);
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

        foreach (var sub in subs)
        {
            try
            {
                var tip = PickRandomTipForCategory(sub.Category ?? "general") ?? localTip;
                if (tip is null) continue;

                var message = $"Developer tip:\n\n{tip.Text}";

                if (sub.SendAudio)
                {
                    var wav = await _tts.SynthesizePreviewToWavFileAsync(message, cancellationToken).ConfigureAwait(false);
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

        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private Tip? PickRandomTipForCategory(string category)
    {
        if (_tips is null || _tips.Count == 0) return null;

        var candidates = _tips.Where(t => t.Tags?.Contains(category, StringComparer.OrdinalIgnoreCase) ?? false).ToList();
        if (!candidates.Any())
        {
            // fallback to any tip tagged general
            candidates = _tips.Where(t => t.Tags?.Contains("general", StringComparer.OrdinalIgnoreCase) ?? false).ToList();
        }

        if (!candidates.Any()) return null;

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
        var tip = PickRandomTipForCategory(category);
        if (tip is null)
        {
            await _telegram.SendMessageInChunksAsync(chatId, "No tips available.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var message = $"Developer tip:\n\n{tip.Text}";

        if (sub?.SendAudio ?? false)
        {
            var wav = await _tts.SynthesizePreviewToWavFileAsync(message, cancellationToken).ConfigureAwait(false);
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
    }
}
