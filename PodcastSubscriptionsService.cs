internal sealed class PodcastSubscriptionsService
{
    private static readonly HashSet<string> NameStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "of", "from", "podcast", "show", "latest", "episode"
    };

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

    public PodcastSubscription? ResolveSubscription(string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return null;
        }

        var exact = TryGetSubscription(requestedName);
        if (exact is not null)
        {
            return exact;
        }

        var normalizedRequested = NormalizeName(requestedName);
        if (string.IsNullOrWhiteSpace(normalizedRequested))
        {
            return null;
        }

        var normalizedExact = _subscriptions.Subscriptions.FirstOrDefault(subscription =>
            string.Equals(NormalizeName(subscription.Name), normalizedRequested, StringComparison.Ordinal));
        if (normalizedExact is not null)
        {
            return normalizedExact;
        }

        var scored = _subscriptions.Subscriptions
            .Select(subscription => new
            {
                Subscription = subscription,
                Score = ScoreNameMatch(requestedName, subscription.Name)
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ToList();

        return scored.FirstOrDefault()?.Subscription;
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

    private static int ScoreNameMatch(string requestedName, string candidateName)
    {
        var normalizedRequested = NormalizeName(requestedName);
        var normalizedCandidate = NormalizeName(candidateName);

        if (string.IsNullOrWhiteSpace(normalizedRequested) || string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return 0;
        }

        var score = 0;

        if (normalizedCandidate.Contains(normalizedRequested, StringComparison.Ordinal))
        {
            score += 70;
        }

        if (normalizedRequested.Contains(normalizedCandidate, StringComparison.Ordinal))
        {
            score += 35;
        }

        var requestedTerms = ExtractTerms(normalizedRequested);
        var candidateTerms = ExtractTerms(normalizedCandidate);
        var overlap = requestedTerms.Intersect(candidateTerms, StringComparer.Ordinal).Count();

        score += overlap * 20;

        return score;
    }

    private static HashSet<string> ExtractTerms(string normalizedValue)
    {
        return normalizedValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3 && !NameStopWords.Contains(term))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();

        return string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
