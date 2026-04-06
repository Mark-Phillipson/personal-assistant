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
        var normalizedText = NormalizePlainTextTableForHtml(text);
        var useMarkdown = ContainsMarkdownFences(normalizedText);
        var parseMode = useMarkdown ? "MarkdownV2" : "HTML";

        var chunks = useMarkdown
            ? ChunkTextPreservingMarkdownCode(normalizedText, 4000)
            : ChunkTextPreservingHtml(normalizedText, 4000);

        foreach (var chunk in chunks)
        {
            try
            {
                await SendMessageAsync(chatId, chunk, cancellationToken, parseMode);
            }
            catch (InvalidOperationException ex) when (useMarkdown ? IsTelegramMarkdownParseError(ex.Message) : IsTelegramHtmlParseError(ex.Message))
            {
                // Telegram parsing failed. Fall back to plain text so the user still receives the content.
                Console.Error.WriteLine($"[telegram.send.fallback] {ex.Message}");

                var plainText = useMarkdown
                    ? ConvertMarkdownToPlainText(chunk)
                    : ConvertHtmlToPlainText(chunk);

                await SendMessageAsync(chatId, plainText, cancellationToken, parseMode: null);
            }
        }
    }

    private static string NormalizePlainTextTableForHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Table strings can be produced as HTML pre blocks (VoiceAdmin, ClipboardHistory, etc.)
        // or as plain pipe tables. Normalize both into a Markdown fenced code block.
        if (ContainsHtmlTags(text))
        {
            var preContent = ExtractPreBlockContent(text);
            if (!string.IsNullOrWhiteSpace(preContent))
            {
                var normalizedTable = preContent.Trim();
                return $"```\n{normalizedTable}\n```";
            }

            var plainText = ConvertHtmlToPlainText(text).Trim();
            if (LooksLikePipeTable(plainText))
            {
                return $"```\n{plainText}\n```";
            }

            return text;
        }

        if (!LooksLikePipeTable(text))
            return text;

        var trimmed = text.Trim();
        return $"```\n{trimmed}\n```";
    }

    private static string? ExtractPreBlockContent(string text)
    {
        var match = Regex.Match(text, "<pre>(.*?)</pre>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool ContainsMarkdownFences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("```", StringComparison.Ordinal);
    }

    private static bool IsFencedMarkdownCodeBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        return trimmed.StartsWith("```", StringComparison.Ordinal) && trimmed.EndsWith("```", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ChunkTextPreservingMarkdownCode(string text, int maxLength)
    {
        // Mixed MarkdownV2 content: just chunk by length.
        // Fenced blocks are handled correctly by Telegram MarkdownV2 as long as we don't split fence delimiters.
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var index = 0;
        while (index < text.Length)
        {
            var fenceStart = text.IndexOf("```", index, StringComparison.Ordinal);
            if (fenceStart < 0)
            {
                foreach (var chunk in ChunkText(text[index..], maxLength))
                    yield return chunk;
                yield break;
            }

            if (fenceStart > index)
            {
                foreach (var chunk in ChunkText(text[index..fenceStart], maxLength))
                    yield return chunk;
            }

            var fenceEnd = text.IndexOf("```", fenceStart + 3, StringComparison.Ordinal);
            if (fenceEnd < 0)
            {
                foreach (var chunk in ChunkText(text[fenceStart..], maxLength))
                    yield return chunk;
                yield break;
            }

            var preBlockEnd = fenceEnd + 3;
            var fencedBlock = text[fenceStart..preBlockEnd];
            foreach (var chunk in ChunkFencedBlock(fencedBlock, maxLength))
                yield return chunk;

            index = preBlockEnd;
        }
    }

    private static IEnumerable<string> ChunkFencedBlock(string fencedBlock, int maxLength)
    {
        if (fencedBlock.Length <= maxLength)
        {
            yield return fencedBlock;
            yield break;
        }

        const string fence = "```";

        var innerStart = fencedBlock.IndexOf(fence, StringComparison.Ordinal) + fence.Length;
        // Trim a leading newline if present
        if (innerStart < fencedBlock.Length && fencedBlock[innerStart] == '\n') innerStart++;
        var innerEnd = fencedBlock.LastIndexOf(fence, StringComparison.Ordinal);
        if (innerStart < fence.Length || innerEnd < innerStart)
        {
            foreach (var chunk in ChunkText(fencedBlock, maxLength))
                yield return chunk;
            yield break;
        }

        var inner = fencedBlock[innerStart..innerEnd];
        var wrapperOverhead = fence.Length * 2 + 2; // ```\n and \n```
        var payloadLimit = Math.Max(1, maxLength - wrapperOverhead);

        var current = new StringBuilder();
        foreach (var line in inner.Split('\n'))
        {
            var normalized = line;

            if (normalized.Length > payloadLimit)
            {
                if (current.Length > 0)
                {
                    yield return fence + "\n" + current + "\n" + fence;
                    current.Clear();
                }

                foreach (var hardChunk in ChunkText(normalized, payloadLimit))
                {
                    yield return fence + "\n" + hardChunk + "\n" + fence;
                }

                continue;
            }

            var projected = current.Length == 0
                ? normalized.Length
                : current.Length + 1 + normalized.Length;

            if (projected > payloadLimit && current.Length > 0)
            {
                yield return fence + "\n" + current + "\n" + fence;
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
            yield return fence + "\n" + current + "\n" + fence;
        }
    }

    private static bool ContainsHtmlTags(string text)
        => Regex.IsMatch(text, "<\\s*/?\\s*[a-zA-Z][^>]*>");

    private static bool LooksLikePipeTable(string text)
    {
        var lines = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (lines.Length < 3)
            return false;

        var pipeLines = lines.Count(line => line.Contains('|'));
        if (pipeLines < 2)
            return false;

        // Detect a separator row: contains at least three hyphens and at least one pipe or plus character.
        return lines.Any(line => line.Count(c => c == '-') >= 3 && (line.Contains('|') || line.Contains('+')));
    }

    private static string EscapeHtml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    private async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, string? parseMode = "HTML")
    {
        var payload = new List<KeyValuePair<string, string>>
        {
            new("chat_id", chatId.ToString()),
            new("text", text),
            new("disable_web_page_preview", "true"),
            new("disable_notification", "true")
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

    public async Task SendDocumentAsync(long chatId, string filePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Document not found", filePath);
        }

        const long maxFileSize = 50L * 1024 * 1024;
        if (fileInfo.Length > maxFileSize)
        {
            throw new InvalidOperationException($"File '{filePath}' exceeds Telegram limit of {maxFileSize} bytes.");
        }

        using var response = await PostMultipartAsyncWithRetry("sendDocument", () =>
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId.ToString()), "chat_id");
            form.Add(new StringContent("true"), "disable_notification");
            var streamContent = new StreamContent(File.OpenRead(filePath));
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(streamContent, "document", fileInfo.Name);
            return form;
        }, cancellationToken);

        var parsed = await response.Content.ReadFromJsonAsync<TelegramApiResponse<JsonElement>>(cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode || parsed is null || !parsed.Ok)
        {
            var description = parsed?.Description ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Telegram sendDocument failed: {description}");
        }
    }

    private async Task<HttpResponseMessage> PostMultipartAsyncWithRetry(
        string endpoint,
        Func<MultipartFormDataContent> contentFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var content = contentFactory();
                return await _httpClient.PostAsync(endpoint, content, cancellationToken);
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

    private static bool IsTelegramMarkdownParseError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // Markdown parsing errors also typically include entity parse errors.
        return IsTelegramHtmlParseError(message) || message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase);
    }

    private static string ConvertMarkdownToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var text = markdown.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[3..^3];
        }

        return text.Trim();
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
