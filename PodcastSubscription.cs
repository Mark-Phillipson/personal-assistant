using System.Text.Json;

internal sealed record PodcastSubscription(string Name, string SearchTerm, string? DirectUrl = null);

internal sealed record PodcastSubscriptions(List<PodcastSubscription> Subscriptions)
{
    public static PodcastSubscriptions FromJsonFile(string path)
    {
        if (!File.Exists(path))
        {
            return new PodcastSubscriptions(new List<PodcastSubscription>());
        }

        try
        {
            using var stream = File.OpenRead(path);
            var overrides = JsonSerializer.Deserialize<PodcastSubscriptionsOverrides>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (overrides?.Subscriptions == null || overrides.Subscriptions.Count == 0)
            {
                return new PodcastSubscriptions(new List<PodcastSubscription>());
            }

            return new PodcastSubscriptions(overrides.Subscriptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[podcast.config.error] Failed to read podcast config at '{path}': {ex.Message}");
            return new PodcastSubscriptions(new List<PodcastSubscription>());
        }
    }

    public async Task SaveToJsonFileAsync(string path)
    {
        try
        {
            var overrides = new PodcastSubscriptionsOverrides { Subscriptions = this.Subscriptions };
            var json = JsonSerializer.Serialize(overrides, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[podcast.config.error] Failed to save podcast config to '{path}': {ex.Message}");
        }
    }

    private sealed class PodcastSubscriptionsOverrides
    {
        public List<PodcastSubscription> Subscriptions { get; init; } = [];
    }
}
