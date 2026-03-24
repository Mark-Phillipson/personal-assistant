using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Speech.Synthesis;

internal sealed class TextToSpeechService
{
    private readonly bool _enabled;
    private readonly int _maxPreviewWords;
    private readonly string _preferredGender;

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

    private TextToSpeechService(bool enabled, int maxPreviewWords, string preferredGender)
    {
        _enabled = enabled;
        _maxPreviewWords = maxPreviewWords;
        _preferredGender = preferredGender?.Trim().ToLowerInvariant() ?? "male";
    }

    public static TextToSpeechService FromEnvironment()
    {
        var enabled = EnvironmentSettings.ReadBool("ASSISTANT_TTS_ENABLED", false);
        var maxPreviewWords = EnvironmentSettings.ReadInt("ASSISTANT_TTS_PREVIEW_MAX_WORDS", 40, 1, 200);
        var preferredGender = EnvironmentSettings.ReadString("ASSISTANT_TTS_PREFERRED_GENDER", "male");
        return new TextToSpeechService(enabled, maxPreviewWords, preferredGender);
    }

    public async Task TrySpeakPreviewAsync(string text, CancellationToken cancellationToken, bool force = false)
    {
        Log($"[tts.debug] enabled={_enabled}, force={force}, isWindows={RuntimeInformation.IsOSPlatform(OSPlatform.Windows)}, textLength={text?.Length}, cancellationRequested={cancellationToken.IsCancellationRequested}");

        if ((!force && !_enabled) || string.IsNullOrWhiteSpace(text) || cancellationToken.IsCancellationRequested)
        {
            var reason = !force && !_enabled ? "disabled" : "empty/canceled";
            if (string.IsNullOrWhiteSpace(text))
            {
                reason = "empty text";
            }
            if (cancellationToken.IsCancellationRequested)
            {
                reason = "cancellation requested";
            }
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

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Log("[tts.info] TTS is only supported on Windows; skipping.");
            return;
        }

        try
        {
            using var synthesizer = new SpeechSynthesizer();
            synthesizer.SetOutputToDefaultAudioDevice();
            Log("[tts.info] Output set to default audio device.");

            var installedVoices = synthesizer.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo)
                .ToArray();

            Log($"[tts.info] Installed voices: {string.Join(", ", installedVoices.Select(v => v.Name + "(" + v.Gender + ")"))}");

            if (!installedVoices.Any())
            {
                Log("[tts.error] No installed voices available.");
                return;
            }

            var selected = (System.Speech.Synthesis.VoiceInfo?)null;
            var zira = installedVoices.FirstOrDefault(v => v.Name.Contains("Zira", StringComparison.OrdinalIgnoreCase));
            if (zira is not null)
            {
                selected = zira;
                Log($"[tts.info] Forcing Zira voice: {selected.Name} ({selected.Gender}).");
            }
            else
            {
                selected = installedVoices.FirstOrDefault(v => string.Equals(v.Gender.ToString(), _preferredGender, StringComparison.OrdinalIgnoreCase));

                if (selected is null)
                {
                    selected = installedVoices.First();
                    Log($"[tts.info] Preferred gender '{_preferredGender}' not found; fallback voice: {selected.Name} ({selected.Gender}).");
                }
                else
                {
                    Log($"[tts.info] Selected voice: {selected.Name} ({selected.Gender}).");
                }
            }

            synthesizer.SelectVoice(selected.Name);
            synthesizer.Rate = -1;
            Log($"[tts.info] Speaking preview ({snippet.Length} chars).");

            // Always also create a fallback WAV file so we can verify output independent of session audio routing.
            var outputFile = Path.Combine(AppContext.BaseDirectory, "tts-output.wav");
            try
            {
                synthesizer.SetOutputToWaveFile(outputFile);
                synthesizer.Speak(snippet);
                Log($"[tts.info] Fallback WAV file written: {outputFile}");
            }
            catch (Exception waveEx)
            {
                Log($"[tts.error] Fallback WAV creation failed: {waveEx.Message}");
            }

            synthesizer.SetOutputToDefaultAudioDevice();
            try
            {
                synthesizer.Speak(snippet);
            }
            catch (Exception speakEx)
            {
                Log($"[tts.error] Direct speak failed: {speakEx.Message}.");
            }
        }
        catch (Exception updateException)
        {
            Log($"[tts.error] Speak failed: {updateException.Message}");
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
