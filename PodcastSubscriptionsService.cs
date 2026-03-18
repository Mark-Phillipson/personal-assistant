internal sealed class PodcastSubscriptionsService
{
    private readonly PodcastSubscriptions _subscriptions;
    private readonly string _configPath;

    private PodcastSubscriptionsService(PodcastSubscriptions subscriptions, string configPath)
    {
        _subscriptions = subscriptions;
        _configPath = configPath;
    }

    public static PodcastSubscriptionsService FromEnvironmentOrJson()
    {
        var configuredPath = EnvironmentSettings.ReadOptionalString("PODCAST_CONFIG_PATH");
        var path = string.IsNullOrWhiteSpace(configuredPath) ? "podcasts.json" : configuredPath;
        var subscriptions = PodcastSubscriptions.FromJsonFile(path);
        return new PodcastSubscriptionsService(subscriptions, path);
    }

    public string GetSetupStatusText()
    {
        var count = _subscriptions.Subscriptions.Count;
        if (count == 0)
        {
            return "No podcasts subscribed.";
        }

        var names = string.Join(", ", _subscriptions.Subscriptions.Select(p => $"\"{p.Name}\""));
        return $"{count} podcast(s) subscribed: {names}";
    }

    public string ListAllSubscriptions()
    {
        if (_subscriptions.Subscriptions.Count == 0)
        {
            return "No podcasts subscribed. Use `/add-podcast <name> <search-term>` to add one.";
        }

        var podcastNames = _subscriptions.Subscriptions.Select(p => p.Name);
        var formatted = TelegramRichTextFormatter.List("🎵 Subscribed Podcasts", podcastNames);
        formatted += "\n\nUse <code>/play-podcast &lt;name&gt; [N]</code> to play the latest (or Nth latest) episode.";
        
        return formatted;
    }

    public PodcastSubscription? TryGetSubscription(string name)
    {
        return _subscriptions.Subscriptions.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public List<PodcastSubscription> GetSubscriptions() => _subscriptions.Subscriptions;

    public async Task AddSubscriptionAsync(string name, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(searchTerm))
        {
            return;
        }

        name = name.Trim();
        searchTerm = searchTerm.Trim();

        // Avoid duplicates
        if (TryGetSubscription(name) != null)
        {
            return;
        }

        _subscriptions.Subscriptions.Add(new PodcastSubscription(name, searchTerm));
        await _subscriptions.SaveToJsonFileAsync(_configPath);
    }
}
