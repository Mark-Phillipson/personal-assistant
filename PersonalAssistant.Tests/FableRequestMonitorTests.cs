using Xunit;

public class FableRequestMonitorTests
{
    [Fact]
    public void IsWithinMonitoringWindow_UsesConfiguredHours()
    {
        Assert.True(FableRequestMonitorService.IsWithinMonitoringWindow(new DateTime(2026, 9, 25, 6, 0, 0), 6, 22));
        Assert.True(FableRequestMonitorService.IsWithinMonitoringWindow(new DateTime(2026, 9, 25, 12, 30, 0), 6, 22));
        Assert.True(FableRequestMonitorService.IsWithinMonitoringWindow(new DateTime(2026, 9, 25, 22, 0, 0), 6, 22));
        Assert.False(FableRequestMonitorService.IsWithinMonitoringWindow(new DateTime(2026, 9, 25, 5, 59, 59), 6, 22));
        Assert.False(FableRequestMonitorService.IsWithinMonitoringWindow(new DateTime(2026, 9, 25, 22, 1, 0), 6, 22));
    }

    [Fact]
    public void NormalizeCheckIntervalMinutes_UsesMinimumAndFallback()
    {
        Assert.Equal(15, FableRequestMonitorService.NormalizeCheckIntervalMinutes(null));
        Assert.Equal(15, FableRequestMonitorService.NormalizeCheckIntervalMinutes("15"));
        Assert.Equal(5, FableRequestMonitorService.NormalizeCheckIntervalMinutes("5"));
        Assert.Equal(15, FableRequestMonitorService.NormalizeCheckIntervalMinutes("0"));
        Assert.Equal(60, FableRequestMonitorService.NormalizeCheckIntervalMinutes("60"));
    }

    [Fact]
    public void ShouldRunImmediateStartupCheck_RespectsFlag()
    {
        Environment.SetEnvironmentVariable("FABLE_RUN_IMMEDIATE_CHECK_ON_START", "true");
        try
        {
            Assert.True(FableRequestMonitorService.ShouldRunImmediateStartupCheck());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FABLE_RUN_IMMEDIATE_CHECK_ON_START", null);
        }
    }

    [Fact]
    public void ShouldAutoLaunchEdgeAtStartup_RespectsFlag()
    {
        Environment.SetEnvironmentVariable("FABLE_AUTO_LAUNCH_EDGE", "false");
        try
        {
            Assert.False(FableRequestMonitorService.ShouldAutoLaunchEdgeAtStartup());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FABLE_AUTO_LAUNCH_EDGE", null);
        }
    }

    [Fact]
    public void EnvironmentSettings_LoadsValuesFromDotEnvFile()
    {
        Environment.SetEnvironmentVariable("FABLE_USERNAME", null);
        Environment.SetEnvironmentVariable("FABLE_PASSWORD", null);
        Environment.SetEnvironmentVariable("FABLE_LOGIN_URL", null);

        EnvironmentSettings.LoadDotEnvIfPresent();

        Assert.Equal("MPhillipson0@Gmail.com", EnvironmentSettings.ReadOptionalString("FABLE_USERNAME"));
        Assert.Equal("Bazooka!9Milan", EnvironmentSettings.ReadOptionalString("FABLE_PASSWORD"));
        Assert.Equal("https://app.makeitfable.com/", EnvironmentSettings.ReadOptionalString("FABLE_LOGIN_URL"));
    }

    [Fact]
    public void EnvironmentSettings_LoadsDotEnvFromParentDirectory()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var nestedDirectory = Path.Combine(repoRoot, "tmp-env-check");
        Directory.CreateDirectory(nestedDirectory);

        try
        {
            Environment.SetEnvironmentVariable("FABLE_USERNAME", null);
            Environment.SetEnvironmentVariable("FABLE_PASSWORD", null);
            Environment.SetEnvironmentVariable("FABLE_LOGIN_URL", null);
            Directory.SetCurrentDirectory(nestedDirectory);

            EnvironmentSettings.LoadDotEnvIfPresent();

            Assert.Equal("MPhillipson0@Gmail.com", EnvironmentSettings.ReadOptionalString("FABLE_USERNAME"));
            Assert.Equal("Bazooka!9Milan", EnvironmentSettings.ReadOptionalString("FABLE_PASSWORD"));
            Assert.Equal("https://app.makeitfable.com/", EnvironmentSettings.ReadOptionalString("FABLE_LOGIN_URL"));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(nestedDirectory, recursive: true);
            Environment.SetEnvironmentVariable("FABLE_USERNAME", null);
            Environment.SetEnvironmentVariable("FABLE_PASSWORD", null);
            Environment.SetEnvironmentVariable("FABLE_LOGIN_URL", null);
        }
    }

    [Fact]
    public void IsFableCheckRequest_RecognizesVoiceCommands()
    {
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("bob check fable"));
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("Bob check fable."));
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("bob check fable now"));
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("bob please check for available requests on fable"));
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("using the fable dashboard check for requests"));
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("check fable requests"));
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("do I have any fable requests"));
        Assert.False(FableRequestMonitorService.IsFableCheckRequest("bob tell me a joke"));
    }
}
