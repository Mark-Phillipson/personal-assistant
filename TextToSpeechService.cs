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

            Log($"[tts.info] Synthesizing text ({snippet.Length} chars) with '{_azureSpeechVoice}' in '{_azureSpeechRegion}'...");

            using var result = await synthesizer.SpeakTextAsync(snippet).ConfigureAwait(false);

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

    private static string ExtractPreviewText(string text, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var plainText = StripHtmlTags(text).Trim();
        var words = Regex.Split(plainText, "\\s+").Where(w => w.Length > 0).ToArray();

        if (words.Length <= maxWords)
        {
            return plainText;
        }

        return string.Join(' ', words.Take(maxWords)) + "...";
    }

    private static string StripHtmlTags(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return Regex.Replace(input, "<.*?>", string.Empty);
    }
}
