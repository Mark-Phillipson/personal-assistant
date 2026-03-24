using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Speech.Synthesis;

internal sealed class TextToSpeechService
{
    private readonly bool _enabled;
    private readonly int _maxPreviewWords;
    private readonly string _preferredGender;

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

    public async Task TrySpeakPreviewAsync(string text, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"[tts.debug] enabled={_enabled}, isWindows={RuntimeInformation.IsOSPlatform(OSPlatform.Windows)}, textLength={text?.Length}, cancellationRequested={cancellationToken.IsCancellationRequested}");

        if (!_enabled || string.IsNullOrWhiteSpace(text) || cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("[tts.debug] early return: disabled/empty/canceled");
            return;
        }

        if (IsLikelyTableContent(text))
        {
            Console.Error.WriteLine("[tts.debug] early return: table content");
            return;
        }

        var snippet = ExtractPreviewText(text, _maxPreviewWords);
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("[tts.info] TTS is only supported on Windows; skipping.");
            return;
        }

        try
        {
            using var synthesizer = new SpeechSynthesizer();
            synthesizer.SetOutputToDefaultAudioDevice();
            Console.Error.WriteLine("[tts.info] Output set to default audio device.");

            var installedVoices = synthesizer.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo)
                .ToArray();

            Console.Error.WriteLine($"[tts.info] Installed voices: {string.Join(", ", installedVoices.Select(v => v.Name + "(" + v.Gender + ")"))}");

            if (!installedVoices.Any())
            {
                Console.Error.WriteLine("[tts.error] No installed voices available.");
                return;
            }

            var selected = (System.Speech.Synthesis.VoiceInfo?)null;
            var zira = installedVoices.FirstOrDefault(v => v.Name.Contains("Zira", StringComparison.OrdinalIgnoreCase));
            if (zira is not null)
            {
                selected = zira;
                Console.Error.WriteLine($"[tts.info] Forcing Zira voice: {selected.Name} ({selected.Gender}).");
            }
            else
            {
                selected = installedVoices.FirstOrDefault(v => string.Equals(v.Gender.ToString(), _preferredGender, StringComparison.OrdinalIgnoreCase));

                if (selected is null)
                {
                    selected = installedVoices.First();
                    Console.Error.WriteLine($"[tts.info] Preferred gender '{_preferredGender}' not found; fallback voice: {selected.Name} ({selected.Gender}).");
                }
                else
                {
                    Console.Error.WriteLine($"[tts.info] Selected voice: {selected.Name} ({selected.Gender}).");
                }
            }

            synthesizer.SelectVoice(selected.Name);
            synthesizer.Rate = -1;
            Console.Error.WriteLine($"[tts.info] Speaking preview ({snippet.Length} chars).");

            // Always also create a fallback WAV file so we can verify output independent of session audio routing.
            var outputFile = Path.Combine(AppContext.BaseDirectory, "tts-output.wav");
            try
            {
                synthesizer.SetOutputToWaveFile(outputFile);
                synthesizer.Speak(snippet);
                Console.Error.WriteLine($"[tts.info] Fallback WAV file written: {outputFile}");
            }
            catch (Exception waveEx)
            {
                Console.Error.WriteLine($"[tts.error] Fallback WAV creation failed: {waveEx.Message}");
            }

            synthesizer.SetOutputToDefaultAudioDevice();
            try
            {
                synthesizer.Speak(snippet);
            }
            catch (Exception speakEx)
            {
                Console.Error.WriteLine($"[tts.error] Direct speak failed: {speakEx.Message}.");
            }
        }
        catch (Exception updateException)
        {
            Console.Error.WriteLine($"[tts.error] Speak failed: {updateException.Message}");
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
