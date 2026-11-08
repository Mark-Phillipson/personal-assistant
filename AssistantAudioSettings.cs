using System;
using System.IO;

internal static class AssistantAudioSettings
{
    private static readonly object Sync = new();
    private static bool _isMuted = LoadMutedState();

    public static bool IsMuted
    {
        get { lock (Sync) return _isMuted; }
    }

    public static void SetMuted(bool muted)
    {
        lock (Sync)
        {
            _isMuted = muted;
            try
            {
                var path = GetSettingsPath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, muted ? "muted" : "enabled");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[audio.settings] Could not persist audio preference: {ex.Message}");
            }
        }
    }

    private static bool LoadMutedState()
    {
        try
        {
            var path = GetSettingsPath();
            return File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), "muted", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio.settings] Could not load audio preference: {ex.Message}");
            return false;
        }
    }

    private static string GetSettingsPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("ASSISTANT_AUDIO_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath.Trim();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(appData) ? AppContext.BaseDirectory : appData;
        return Path.Combine(root, "PersonalAssistant", "audio-settings.txt");
    }
}
