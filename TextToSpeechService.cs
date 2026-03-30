using System.Text.RegularExpressions;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

internal sealed class TextToSpeechService
{
    private readonly bool _enabled;
    private readonly int _maxPreviewWords;
    private readonly string _preferredGender;
    private readonly string? _azureSpeechKey;
    private readonly string? _azureSpeechRegion;
    private readonly string _azureSpeechVoice;
    private readonly PronunciationDictionaryService? _pronunciationService;
    private static readonly Regex MarkdownBoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MarkdownItalicRegex = new(@"\*(.+?)\*", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MarkdownFormattingRegex = new(@"\*\*(?<bold>.+?)\*\*|\*(?<italic>.+?)\*", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "tts-debug.log");

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        try
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // ignore file logging failures to avoid affecting TTS flow.
        }
        Console.Error.WriteLine(line);
    }

    private TextToSpeechService(bool enabled, int maxPreviewWords, string preferredGender, string? azureSpeechKey, string? azureSpeechRegion, string azureSpeechVoice, PronunciationDictionaryService? pronunciationService = null)
    {
        _enabled = enabled;
        _maxPreviewWords = maxPreviewWords;
        _preferredGender = preferredGender?.Trim().ToLowerInvariant() ?? "male";
        _azureSpeechKey = azureSpeechKey;
        _azureSpeechRegion = azureSpeechRegion;
        _azureSpeechVoice = azureSpeechVoice;
        _pronunciationService = pronunciationService;
    }

    public static TextToSpeechService FromEnvironment(PronunciationDictionaryService? pronunciationService = null)
    {
        var enabled = EnvironmentSettings.ReadBool("ASSISTANT_TTS_ENABLED", false);
        var maxPreviewWords = EnvironmentSettings.ReadInt("ASSISTANT_TTS_PREVIEW_MAX_WORDS", 40, 1, 200);
        var preferredGender = EnvironmentSettings.ReadString("ASSISTANT_TTS_PREFERRED_GENDER", "male");
        var azureSpeechKey = EnvironmentSettings.ReadOptionalString("AZURE_SPEECH_KEY");
        var azureSpeechRegion = EnvironmentSettings.ReadOptionalString("AZURE_SPEECH_REGION");
        var azureSpeechVoice = EnvironmentSettings.ReadString("AZURE_SPEECH_VOICE", "en-GB-RyanNeural");

        return new TextToSpeechService(enabled, maxPreviewWords, preferredGender, azureSpeechKey, azureSpeechRegion, azureSpeechVoice, pronunciationService);
    }

    public async Task TrySpeakPreviewAsync(string text, CancellationToken cancellationToken, bool force = false)
    {
        Log($"[tts.debug] enabled={_enabled}, force={force}, textLength={text?.Length}, cancellationRequested={cancellationToken.IsCancellationRequested}, region={_azureSpeechRegion}, voice={_azureSpeechVoice}");

        if ((!force && !_enabled) || string.IsNullOrWhiteSpace(text) || cancellationToken.IsCancellationRequested)
        {
            var reason = !force && !_enabled ? "disabled" : "empty/canceled";
            if (string.IsNullOrWhiteSpace(text)) reason = "empty text";
            if (cancellationToken.IsCancellationRequested) reason = "cancellation requested";
            Log($"[tts.debug] early return: {reason}");
            return;
        }

        if (IsLikelyTableContent(text))
        {
            Log("[tts.debug] early return: table content");
            return;
        }

        var snippet = ExtractPreviewText(text, _maxPreviewWords);
        Log($"[tts.debug] extracted snippet ({snippet?.Length} chars): '{snippet}'");
        if (string.IsNullOrWhiteSpace(snippet))
        {
            Log("[tts.debug] early return: snippet empty after extraction");
            return;
        }

        // Apply pronunciation corrections if service is available.
        if (_pronunciationService != null)
        {
            var (correctedSnippet, appliedCorrections) = _pronunciationService.ApplyCorrections(snippet);
            if (appliedCorrections.Any())
            {
                Log($"[tts.info] Applied {appliedCorrections.Count} pronunciation correction(s): {string.Join(", ", appliedCorrections.Keys)}");
                snippet = correctedSnippet;
            }
        }

        if (string.IsNullOrWhiteSpace(_azureSpeechKey))
        {
            Log("[tts.warn] AZURE_SPEECH_KEY is missing; skipping TTS.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_azureSpeechRegion))
        {
            Log("[tts.warn] AZURE_SPEECH_REGION is missing; skipping TTS.");
            return;
        }

        try
        {
            var speechConfig = SpeechConfig.FromSubscription(_azureSpeechKey, _azureSpeechRegion);
            speechConfig.SpeechSynthesisVoiceName = _azureSpeechVoice;

            using var audioConfig = AudioConfig.FromDefaultSpeakerOutput();
            using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig);
            var speechInput = BuildSpeechInput(snippet, _azureSpeechVoice);

            Log($"[tts.info] Synthesizing {(speechInput.IsSsml ? "ssml" : "text")} ({speechInput.Content.Length} chars) with '{_azureSpeechVoice}' in '{_azureSpeechRegion}'...");

            using var result = speechInput.IsSsml
                ? await synthesizer.SpeakSsmlAsync(speechInput.Content).ConfigureAwait(false)
                : await synthesizer.SpeakTextAsync(speechInput.Content).ConfigureAwait(false);

            if (result.Reason == ResultReason.SynthesizingAudioCompleted)
            {
                Log($"[tts.info] Azure TTS success.");
            }
            else if (result.Reason == ResultReason.Canceled)
            {
                var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                Log($"[tts.error] Azure TTS canceled: Code={cancellation.ErrorCode}, Details={cancellation.ErrorDetails}");
            }
            else
            {
                Log($"[tts.error] Azure TTS failed with reason {result.Reason}");
            }
        }
        catch (Exception ex)
        {
            Log($"[tts.error] Azure TTS failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    public async Task<string?> SynthesizePreviewToWavFileAsync(string text, CancellationToken cancellationToken, bool force = false)
    {
        Log($"[tts.debug] synth-to-file enabled={_enabled}, force={force}, textLength={text?.Length}, cancellationRequested={cancellationToken.IsCancellationRequested}");

        if ((!force && !_enabled) || string.IsNullOrWhiteSpace(text) || cancellationToken.IsCancellationRequested)
        {
            var reason = !force && !_enabled ? "disabled" : "empty/canceled";
            if (string.IsNullOrWhiteSpace(text)) reason = "empty text";
            if (cancellationToken.IsCancellationRequested) reason = "cancellation requested";
            Log($"[tts.debug] synth-to-file early return: {reason}");
            return null;
        }

        if (IsLikelyTableContent(text))
        {
            Log("[tts.debug] synth-to-file early return: table content");
            return null;
        }

        var speechText = ExtractSpeechText(text, null);
        Log($"[tts.debug] synth-to-file extracted text ({speechText?.Length} chars): '{speechText}'");
        if (string.IsNullOrWhiteSpace(speechText))
        {
            Log("[tts.debug] synth-to-file early return: speech text empty after extraction");
            return null;
        }

        // Apply pronunciation corrections if service is available.
        if (_pronunciationService != null)
        {
            var (correctedText, appliedCorrections) = _pronunciationService.ApplyCorrections(speechText);
            if (appliedCorrections.Any())
            {
                Log($"[tts.info] Applied {appliedCorrections.Count} pronunciation correction(s): {string.Join(", ", appliedCorrections.Keys)}");
                speechText = correctedText;
            }
        }

        if (string.IsNullOrWhiteSpace(_azureSpeechKey))
        {
            Log("[tts.warn] AZURE_SPEECH_KEY is missing; skipping TTS to file.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(_azureSpeechRegion))
        {
            Log("[tts.warn] AZURE_SPEECH_REGION is missing; skipping TTS to file.");
            return null;
        }

        try
        {
            var speechConfig = SpeechConfig.FromSubscription(_azureSpeechKey, _azureSpeechRegion);
            speechConfig.SpeechSynthesisVoiceName = _azureSpeechVoice;

            var tempFile = Path.Combine(Path.GetTempPath(), $"assistant_tts_{Guid.NewGuid()}.wav");

            using var audioConfig = AudioConfig.FromWavFileOutput(tempFile);
            using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig);
            var speechInput = BuildSpeechInput(speechText, _azureSpeechVoice);

            Log($"[tts.info] Synthesizing to file '{tempFile}' with '{_azureSpeechVoice}' in '{_azureSpeechRegion}'...");

            using var result = speechInput.IsSsml
                ? await synthesizer.SpeakSsmlAsync(speechInput.Content).ConfigureAwait(false)
                : await synthesizer.SpeakTextAsync(speechInput.Content).ConfigureAwait(false);

            if (result.Reason == ResultReason.SynthesizingAudioCompleted)
            {
                Log($"[tts.info] Azure TTS file synthesis success: {tempFile}");
                return tempFile;
            }
            else if (result.Reason == ResultReason.Canceled)
            {
                var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                Log($"[tts.error] Azure TTS canceled: Code={cancellation.ErrorCode}, Details={cancellation.ErrorDetails}");
            }
            else
            {
                Log($"[tts.error] Azure TTS failed with reason {result.Reason}");
            }

            // If synthesis didn't complete, ensure no orphan file remains.
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            Log($"[tts.error] Azure TTS file synthesis failed: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }

    private static bool IsLikelyTableContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains("<pre>", StringComparison.OrdinalIgnoreCase) || text.Contains("</pre>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3)
        {
            return false;
        }

        var pipeLineCount = lines.Count(line => line.Contains('|'));
        if (pipeLineCount < 3)
        {
            return false;
        }

        var separatorPattern = "^-{3,}(?:[|+][-: ]{2,})+[-]*$";
        if (lines.Any(line => Regex.IsMatch(line, separatorPattern)))
        {
            return true;
        }

        return false;
    }

    // Matches http/https/ftp URLs including markdown link syntax [text](url).
    private static readonly Regex UrlRegex = new(
        @"(?:\[[^\]]*\]\s*)?\(?(https?://|ftp://)\S+\)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches Unicode emoji: emoticons, symbols, pictographs, transport, flags, etc.
    private static readonly Regex EmojiRegex = new(
        @"[\u00A9\u00AE\u203C\u2049\u20E3\u2122\u2139\u2194-\u2199\u21A9-\u21AA\u231A-\u231B\u2328\u23CF\u23E9-\u23F3\u23F8-\u23FA\u24C2\u25AA-\u25AB\u25B6\u25C0\u25FB-\u25FE\u2600-\u2604\u260E\u2611\u2614-\u2615\u2618\u261D\u2620\u2622-\u2623\u2626\u262A\u262E-\u262F\u2638-\u263A\u2640\u2642\u2648-\u2653\u265F-\u2660\u2663\u2665-\u2666\u2668\u267B\u267E-\u267F\u2692-\u2697\u2699\u269B-\u269C\u26A0-\u26A1\u26A7\u26AA-\u26AB\u26B0-\u26B1\u26BD-\u26BE\u26C4-\u26C5\u26CE-\u26CF\u26D1\u26D3-\u26D4\u26E9-\u26EA\u26F0-\u26F5\u26F7-\u26FA\u26FD\u2702\u2705\u2708-\u270D\u270F\u2712\u2714\u2716\u271D\u2721\u2728\u2733-\u2734\u2744\u2747\u274C\u274E\u2753-\u2755\u2757\u2763-\u2764\u2795-\u2797\u27A1\u27B0\u27BF\u2934-\u2935\u2B05-\u2B07\u2B1B-\u2B1C\u2B50\u2B55\u3030\u303D\u3297\u3299]" +
        @"|[\uD83C][\uDC00-\uDFFF]|[\uD83D][\uDC00-\uDFFF]|[\uD83E][\uDD00-\uDFFF]" +
        @"|[\uD83C][\uDDE0-\uDDFF][\uD83C][\uDDE0-\uDDFF]",  // flag sequences
        RegexOptions.Compiled);

    private static string ExtractSpeechText(string text, int? maxWords = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var plainText = StripHtmlTags(text).Trim();
        plainText = UrlRegex.Replace(plainText, "web address");
        plainText = EmojiRegex.Replace(plainText, string.Empty);
        // Collapse any whitespace gaps left by removals.
        plainText = Regex.Replace(plainText, @"\s{2,}", " ").Trim();

        if (!maxWords.HasValue)
        {
            return plainText;
        }

        var words = Regex.Split(plainText, "\\s+").Where(w => w.Length > 0).ToArray();

        if (words.Length <= maxWords.Value)
        {
            return plainText;
        }

        return string.Join(' ', words.Take(maxWords.Value)) + "...";
    }

    private static string ExtractPreviewText(string text, int maxWords)
    {
        return ExtractSpeechText(text, maxWords);
    }

    private static string StripHtmlTags(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return Regex.Replace(input, "<.*?>", string.Empty);
    }

    private static SpeechInput BuildSpeechInput(string text, string voiceName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SpeechInput(string.Empty, IsSsml: false);
        }

        // If no markdown formatting is present, strip stray markers and return plain text.
        var hasBold = MarkdownBoldRegex.IsMatch(text);
        var hasItalic = MarkdownItalicRegex.IsMatch(text);
        if (!hasBold && !hasItalic)
        {
            return new SpeechInput(text.Replace("**", string.Empty).Replace("*", string.Empty), IsSsml: false);
        }

        var sb = new System.Text.StringBuilder();
        var lastIndex = 0;

        foreach (Match match in MarkdownFormattingRegex.Matches(text))
        {
            var prefix = text[lastIndex..match.Index].Replace("**", string.Empty).Replace("*", string.Empty);
            sb.Append(EscapeXml(prefix));

            var emphasizedText = match.Groups["bold"].Success
                ? match.Groups["bold"].Value
                : match.Groups["italic"].Value;

            if (!string.IsNullOrWhiteSpace(emphasizedText))
            {
                sb.Append("<break time=\"60ms\"/>");
                sb.Append("<emphasis level=\"moderate\">");
                sb.Append(EscapeXml(emphasizedText));
                sb.Append("</emphasis>");
                sb.Append("<break time=\"40ms\"/>");
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            var suffix = text[lastIndex..].Replace("**", string.Empty).Replace("*", string.Empty);
            sb.Append(EscapeXml(suffix));
        }

        var safeVoiceName = EscapeXml(string.IsNullOrWhiteSpace(voiceName) ? "en-GB-RyanNeural" : voiceName);
        var ssml = $"<speak version=\"1.0\" xml:lang=\"en-GB\"><voice name=\"{safeVoiceName}\">{sb}</voice></speak>";
        return new SpeechInput(ssml, IsSsml: true);
    }

    private static string EscapeXml(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private readonly record struct SpeechInput(string Content, bool IsSsml);
}
