using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        foreach (var chunk in ChunkTextPreservingHtml(text, 4000))
        {
            try
            {
                await SendMessageAsync(chatId, chunk, cancellationToken);
            }
            catch (InvalidOperationException ex) when (IsTelegramHtmlParseError(ex.Message))
            {
                // Telegram HTML mode rejects unsupported tags (for example <table>) and malformed entities.
                // Fall back to plain text so the user still receives the content instead of a hard failure.
                Console.Error.WriteLine($"[telegram.send.html_fallback] {ex.Message}");
                var plainText = ConvertHtmlToPlainText(chunk);
                await SendMessageAsync(chatId, plainText, cancellationToken, parseMode: null);
            }
        }
    }

    private async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, string? parseMode = "HTML")
    {
        var payload = new List<KeyValuePair<string, string>>
        {
            new("chat_id", chatId.ToString()),
            new("text", text),
            new("disable_web_page_preview", "true")
        };

        if (!string.IsNullOrWhiteSpace(parseMode))
        {
            payload.Add(new("parse_mode", parseMode));
        }

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
                Console.Error.WriteLine($"[telegram.transport.retry] endpoint={endpoint} attempt={attempt}/{MaxTransportAttempts} delayMs={(int)delay.TotalMilliseconds} error={FormatExceptionChain(ex)}");
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
                Console.Error.WriteLine($"[telegram.transport.retry] endpoint={requestUri} attempt={attempt}/{MaxTransportAttempts} delayMs={(int)delay.TotalMilliseconds} error={FormatExceptionChain(ex)}");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsTransientTransportFailure(HttpRequestException exception)
    {
        return exception.InnerException is IOException
            or SocketException;
    }

    private static string FormatExceptionChain(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        var index = 0;

        while (current is not null)
        {
            if (index > 0)
            {
                builder.Append(" -> ");
            }

            builder.Append(current.GetType().Name);

            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                builder.Append(": ");
                builder.Append(current.Message);
            }

            current = current.InnerException;
            index++;
        }

        return builder.ToString();
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

    private static IEnumerable<string> ChunkTextPreservingHtml(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var index = 0;
        while (index < text.Length)
        {
            var preStart = text.IndexOf("<pre>", index, StringComparison.OrdinalIgnoreCase);
            if (preStart < 0)
            {
                foreach (var chunk in ChunkText(text[index..], maxLength))
                {
                    yield return chunk;
                }
                yield break;
            }

            if (preStart > index)
            {
                foreach (var chunk in ChunkText(text[index..preStart], maxLength))
                {
                    yield return chunk;
                }
            }

            var preEndTagIndex = text.IndexOf("</pre>", preStart, StringComparison.OrdinalIgnoreCase);
            if (preEndTagIndex < 0)
            {
                foreach (var chunk in ChunkText(text[preStart..], maxLength))
                {
                    yield return chunk;
                }
                yield break;
            }

            var preBlockEnd = preEndTagIndex + "</pre>".Length;
            var preBlock = text[preStart..preBlockEnd];
            foreach (var chunk in ChunkPreBlock(preBlock, maxLength))
            {
                yield return chunk;
            }

            index = preBlockEnd;
        }
    }

    private static IEnumerable<string> ChunkPreBlock(string preBlock, int maxLength)
    {
        if (preBlock.Length <= maxLength)
        {
            yield return preBlock;
            yield break;
        }

        const string openTag = "<pre>";
        const string closeTag = "</pre>";

        var innerStart = preBlock.IndexOf(openTag, StringComparison.OrdinalIgnoreCase) + openTag.Length;
        var innerEnd = preBlock.LastIndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
        if (innerStart < openTag.Length || innerEnd < innerStart)
        {
            foreach (var chunk in ChunkText(preBlock, maxLength))
            {
                yield return chunk;
            }
            yield break;
        }

        var inner = preBlock[innerStart..innerEnd];
        var wrapperOverhead = openTag.Length + closeTag.Length;
        var payloadLimit = Math.Max(1, maxLength - wrapperOverhead);

        var current = new StringBuilder();
        foreach (var line in inner.Split('\n'))
        {
            var normalized = line;

            if (normalized.Length > payloadLimit)
            {
                if (current.Length > 0)
                {
                    yield return $"{openTag}{current}{closeTag}";
                    current.Clear();
                }

                foreach (var hardChunk in ChunkText(normalized, payloadLimit))
                {
                    yield return $"{openTag}{hardChunk}{closeTag}";
                }

                continue;
            }

            var projected = current.Length == 0
                ? normalized.Length
                : current.Length + 1 + normalized.Length;

            if (projected > payloadLimit && current.Length > 0)
            {
                yield return $"{openTag}{current}{closeTag}";
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(normalized);
        }

        if (current.Length > 0)
        {
            yield return $"{openTag}{current}{closeTag}";
        }
    }

    private static bool IsTelegramHtmlParseError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported start tag", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported end tag", StringComparison.OrdinalIgnoreCase)
            || message.Contains("tag", StringComparison.OrdinalIgnoreCase) && message.Contains("not allowed", StringComparison.OrdinalIgnoreCase);
    }

    private static string ConvertHtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var text = html
            .Replace("<pre>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</pre>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<b>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</b>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<i>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</i>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<u>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</u>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<s>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</s>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<code>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</code>", string.Empty, StringComparison.OrdinalIgnoreCase);

        text = Regex.Replace(text, "<[^>]+>", string.Empty);

        return text
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
