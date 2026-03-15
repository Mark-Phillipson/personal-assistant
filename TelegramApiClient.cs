using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

internal sealed class TelegramApiClient : IDisposable
{
    private const int MaxTransportAttempts = 3;
    private readonly string _botToken;
    private readonly HttpClient _httpClient;

    public TelegramApiClient(string botToken)
    {
        _botToken = botToken;
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

        using var response = await PostAsyncWithRetry("getUpdates", parameters, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<TelegramApiResponse<List<TelegramUpdate>>>(cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode || payload is null || !payload.Ok || payload.Result is null)
        {
            var description = payload?.Description ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Telegram getUpdates failed: {description}");
        }

        return payload.Result;
    }

    public async Task<TelegramRemoteFile> GetFileAsync(string fileId, CancellationToken cancellationToken)
    {
        var payload = new List<KeyValuePair<string, string>>
        {
            new("file_id", fileId)
        };

        using var response = await PostAsyncWithRetry("getFile", payload, cancellationToken);
        var parsed = await response.Content.ReadFromJsonAsync<TelegramApiResponse<TelegramRemoteFile>>(cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode || parsed is null || !parsed.Ok || parsed.Result is null)
        {
            var description = parsed?.Description ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Telegram getFile failed: {description}");
        }

        return parsed.Result;
    }

    public async Task DownloadFileAsync(string filePath, string destinationPath, CancellationToken cancellationToken)
    {
        var downloadUri = new Uri($"https://api.telegram.org/file/bot{_botToken}/{filePath}");

        using var response = await GetAsyncWithRetry(downloadUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var targetDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputStream = File.Create(destinationPath);
        await responseStream.CopyToAsync(outputStream, cancellationToken);
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

        using var response = await PostAsyncWithRetry("sendMessage", payload, cancellationToken);
        var parsed = await response.Content.ReadFromJsonAsync<TelegramApiResponse<JsonElement>>(cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode || parsed is null || !parsed.Ok)
        {
            var description = parsed?.Description ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Telegram sendMessage failed: {description}");
        }
    }

    private async Task<HttpResponseMessage> PostAsyncWithRetry(
        string endpoint,
        IReadOnlyCollection<KeyValuePair<string, string>> formValues,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _httpClient.PostAsync(endpoint, new FormUrlEncodedContent(formValues), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < MaxTransportAttempts && IsTransientTransportFailure(ex))
            {
                var delay = TimeSpan.FromMilliseconds(500 * attempt);
                Console.Error.WriteLine($"[telegram.transport.retry] endpoint={endpoint} attempt={attempt}/{MaxTransportAttempts} delayMs={(int)delay.TotalMilliseconds} error={ex.Message}");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task<HttpResponseMessage> GetAsyncWithRetry(Uri requestUri, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _httpClient.GetAsync(requestUri, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < MaxTransportAttempts && IsTransientTransportFailure(ex))
            {
                var delay = TimeSpan.FromMilliseconds(500 * attempt);
                Console.Error.WriteLine($"[telegram.transport.retry] endpoint={requestUri} attempt={attempt}/{MaxTransportAttempts} delayMs={(int)delay.TotalMilliseconds} error={ex.Message}");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsTransientTransportFailure(HttpRequestException exception)
    {
        return exception.InnerException is IOException
            or SocketException;
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
