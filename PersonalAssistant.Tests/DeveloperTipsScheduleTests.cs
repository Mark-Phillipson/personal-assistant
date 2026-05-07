using System.Text.Json;
using Xunit;

public class DeveloperTipsScheduleTests
{
    [Fact]
    public async Task ScheduleSetAndPersist()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "devtips-schedule-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);

        var tipsPath = Path.Combine(tmpDir, "developer-tips.json");
        var statePath = Path.Combine(tmpDir, "dev-tips-state.json");

        var tipsJson = "[ { \"id\": \"t1\", \"text\": \"Tip one\", \"tags\": [\"general\"] } ]";
        await File.WriteAllTextAsync(tipsPath, tipsJson);

        var tts = TextToSpeechService.FromEnvironment();
        using var telegram = new TelegramApiClient("TEST_TOKEN");

        var svc = new DeveloperTipsService(tts, telegram, tipsFilePath: tipsPath, stateFilePath: statePath);
        await svc.LoadAsync();

        await svc.SetScheduleFixedAsync(15);
        var raw = await File.ReadAllTextAsync(statePath);
        Assert.Contains("\"scheduleMode\"", raw);
        Assert.Contains("\"frequencyMinutes\": 15", raw);

        await svc.SetScheduleRandomAsync(5, 10);
        raw = await File.ReadAllTextAsync(statePath);
        Assert.Contains("\"scheduleMode\": \"random\"", raw);
        Assert.Contains("\"randomMinMinutes\": 5", raw);
        Assert.Contains("\"randomMaxMinutes\": 10", raw);

        await svc.SetScheduleTimesAsync(new[] { TimeSpan.Parse("09:15"), TimeSpan.Parse("18:30") });
        raw = await File.ReadAllTextAsync(statePath);
        Assert.Contains("\"scheduleMode\": \"times\"", raw);
        Assert.Contains("09:15", raw);
        Assert.Contains("18:30", raw);
    }
}
