using System.Text.Json;
using Xunit;

public class DeveloperTipsAnnounceTests
{
    [Fact]
    public async Task AnnouncesTipLocally_WhenTtsConfigured()
    {
        var azureKey = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var azureRegion = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
        var ttsEnabledStr = Environment.GetEnvironmentVariable("ASSISTANT_TTS_ENABLED");
        if (string.IsNullOrWhiteSpace(azureKey) || string.IsNullOrWhiteSpace(azureRegion) || !bool.TryParse(ttsEnabledStr, out var ttsEnabled) || !ttsEnabled)
        {
            // Skip the test when Azure TTS isn't configured locally.
            return;
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), "devtips-announce-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);

        var tipsPath = Path.Combine(tmpDir, "developer-tips.json");
        var statePath = Path.Combine(tmpDir, "dev-tips-state.json");

        var tipsJson = "[ { \"id\": \"t1\", \"text\": \"This is a spoken test tip.\", \"tags\": [\"general\"] } ]";
        await File.WriteAllTextAsync(tipsPath, tipsJson);

        var tts = TextToSpeechService.FromEnvironment();
        using var telegram = new TelegramApiClient("TEST_TOKEN");

        var svc = new DeveloperTipsService(tts, telegram, tipsFilePath: tipsPath, stateFilePath: statePath);
        await svc.LoadAsync();

        // Ensure no subscriber audio send to avoid network calls; we only want local speech.
        var chatId = 123456789L;
        await svc.DisableForChatAsync(chatId);

        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        await svc.ForceAnnounceForChatAsync(chatId, cts.Token);

        // Verify state file was written with a lastTipId
        var raw = await File.ReadAllTextAsync(statePath);
        var doc = JsonDocument.Parse(raw);
        Assert.True(doc.RootElement.TryGetProperty("lastTipId", out var lastTipId));
        Assert.False(string.IsNullOrWhiteSpace(lastTipId.GetString()));
    }
}
