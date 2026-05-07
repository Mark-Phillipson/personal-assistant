using System.Text.Json;
using Xunit;

public class DeveloperTipsTests
{
    [Fact]
    public async Task EnableDisablePersistRoundTrip()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "devtips-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);

        var tipsPath = Path.Combine(tmpDir, "developer-tips.json");
        var statePath = Path.Combine(tmpDir, "dev-tips-state.json");

        var tipsJson = "[ { \"id\": \"t1\", \"text\": \"Tip one\", \"tags\": [\"general\"] } ]";
        await File.WriteAllTextAsync(tipsPath, tipsJson);

        var tts = TextToSpeechService.FromEnvironment();
        using var telegram = new TelegramApiClient("TEST_TOKEN");

        var svc = new DeveloperTipsService(tts, telegram, tipsFilePath: tipsPath, stateFilePath: statePath);
        await svc.LoadAsync();

        // Subscribe chat
        var chatId = 123456789L;
        await svc.EnableForChatAsync(chatId, category: "general", sendAudio: false);
        var status = svc.GetStatusForChat(chatId);
        Assert.Contains("Subscribed: yes", status);
        Assert.Contains("Category: general", status);

        // Toggle send audio
        await svc.SetSendAudioForChatAsync(chatId, true);
        status = svc.GetStatusForChat(chatId);
        Assert.Contains("SendAudio: True", status, StringComparison.OrdinalIgnoreCase);

        // Persisted file should exist and contain the subscriber
        Assert.True(File.Exists(statePath));
        var raw = await File.ReadAllTextAsync(statePath);
        var doc = JsonDocument.Parse(raw);
        Assert.True(doc.RootElement.TryGetProperty("subscribers", out var subs));
        Assert.True(subs.GetArrayLength() >= 1);

        // Disable
        await svc.DisableForChatAsync(chatId);
        status = svc.GetStatusForChat(chatId);
        Assert.Equal("Not subscribed to developer tips.", status);

        // Persisted file should no longer list subscriber
        raw = await File.ReadAllTextAsync(statePath);
        doc = JsonDocument.Parse(raw);
        subs = doc.RootElement.GetProperty("subscribers");
        Assert.True(subs.GetArrayLength() == 0);
    }
}
