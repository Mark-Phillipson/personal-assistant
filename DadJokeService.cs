using System.Net.Http.Headers;
using System.Text.Json;

internal sealed class DadJokeService
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DadJokeService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        // icanhazdadjoke requires a User-Agent header for proper usage.
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("personal-assistant/1.0 (https://github.com/Mark-Phillipson/personal-assistant)");
    }

    public async Task<string> GetRandomJokeAsync(CancellationToken cancellationToken = default)
    {
        return await GetJokeAsync(null, cancellationToken);
    }

    public async Task<string> GetJokeAsync(string? searchTerm, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(searchTerm)
                ? "https://icanhazdadjoke.com/"
                : $"https://icanhazdadjoke.com/search?term={Uri.EscapeDataString(searchTerm)}&limit=30";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return $"I couldn't fetch a joke right now (empty response, status {response.StatusCode}). Try again in a moment.";
            }

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                var parsed = JsonSerializer.Deserialize<DadJokeResponse>(json, JsonOptions);
                if (parsed?.Joke is not null)
                {
                    return parsed.Joke;
                }

                return $"I couldn't fetch a joke right now (unexpected response: {json}). Try again in a moment.";
            }

            var searchResponse = JsonSerializer.Deserialize<DadJokeSearchResponse>(json, JsonOptions);
            if (searchResponse is null || searchResponse.Results is null || searchResponse.Results.Count == 0)
            {
                return "I couldn't find a joke matching that search. Try something else or just ask for a random dad joke.";
            }

            // Choose a random joke from the search results to keep it fresh.
            var random = new Random();
            var joke = searchResponse.Results[random.Next(searchResponse.Results.Count)];
            return joke.Joke;
        }
        catch (Exception ex)
        {
            return $"I couldn't fetch a dad joke right now: {ex.Message}";
        }
    }

    private sealed record DadJokeResponse(string Id, string Joke, int Status);

    private sealed record DadJokeSearchResponse(int CurrentPage, int Limit, int NextPage, int PreviousPage, int ResultsPerPage, int TotalJokes, List<DadJokeResponse>? Results);
}
