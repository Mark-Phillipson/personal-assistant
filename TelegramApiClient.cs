using System.Net.Http.Json;
using System.Text.Json;

internal sealed class TelegramApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public TelegramApiClient(string botToken)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{botToken}/")
        };
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long? offset,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("timeout", timeoutSeconds.ToString()),
            new("allowed_updates", "[\"message\"]")
        };

        if (offset.HasValue)
        {
            parameters.Add(new("offset", offset.Value.ToString()));
        }

        using var response = await _httpClient.PostAsync("getUpdates", new FormUrlEncodedContent(parameters), cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<TelegramApiResponse<List<TelegramUpdate>>>(cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode || payload is null || !payload.Ok || payload.Result is null)
        {
            var description = payload?.Description ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Telegram getUpdates failed: {description}");
        }

        return payload.Result;
    }

    public async Task SendMessageInChunksAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        foreach (var chunk in ChunkText(text, 4000))
        {
            await SendMessageAsync(chatId, chunk, cancellationToken);
        }
    }

    private async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var payload = new List<KeyValuePair<string, string>>
        {
            new("chat_id", chatId.ToString()),
            new("text", text),
            new("disable_web_page_preview", "true")
        };

        using var response = await _httpClient.PostAsync("sendMessage", new FormUrlEncodedContent(payload), cancellationToken);
        var parsed = await response.Content.ReadFromJsonAsync<TelegramApiResponse<JsonElement>>(cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode || parsed is null || !parsed.Ok)
        {
            var description = parsed?.Description ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Telegram sendMessage failed: {description}");
        }
    }

    private static IEnumerable<string> ChunkText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(maxLength, text.Length - start);
            yield return text.Substring(start, length);
            start += length;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
