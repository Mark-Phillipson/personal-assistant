using System.Diagnostics;
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
    public void ShouldAutoLaunchEdgeAtStartup_DefaultsToDisabled()
    {
        Environment.SetEnvironmentVariable("FABLE_AUTO_LAUNCH_EDGE", null);
        Assert.False(FableRequestMonitorService.ShouldAutoLaunchEdgeAtStartup());
    }

    [Fact]
    public void ShouldAutoLaunchEdgeAtStartup_RespectsFlag()
    {
        Environment.SetEnvironmentVariable("FABLE_AUTO_LAUNCH_EDGE", "true");
        try
        {
            Assert.True(FableRequestMonitorService.ShouldAutoLaunchEdgeAtStartup());
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
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("fable"));
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("Voice command: check fable for requests"));
        Assert.True(FableRequestMonitorService.IsFableCheckRequest("what are my fable requests"));
        Assert.False(FableRequestMonitorService.IsFableCheckRequest("what is fable"));
        Assert.False(FableRequestMonitorService.IsFableCheckRequest("bob tell me a joke"));
    }

    [Fact]
    public void BrowserMonitorConfig_DefaultsToFableMonitor()
    {
        var sites = BrowserMonitorService.GetConfiguredMonitorSites();

        Assert.NotEmpty(sites);
        Assert.Contains(sites, site => string.Equals(site.Name, "Fable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sites, site => string.Equals(site.LoginUrl, "https://app.makeitfable.com/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrowserMonitorConfig_UsesHeadfulFallbackWhenRequested()
    {
        Environment.SetEnvironmentVariable("BROWSER_MONITOR_HEADFUL_LOGIN_ON_FAILURE", "true");
        try
        {
            Assert.True(BrowserMonitorService.ShouldLaunchHeadfulLoginFallback());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BROWSER_MONITOR_HEADFUL_LOGIN_ON_FAILURE", null);
        }
    }

    [Fact]
    public void BrowserMonitorConfig_UsesHeadlessByDefaultAndVisibleWhenRequested()
    {
        Environment.SetEnvironmentVariable("BROWSER_MONITOR_HEADLESS", null);
        Environment.SetEnvironmentVariable("BROWSER_MONITOR_VISIBLE", null);
        Assert.True(BrowserMonitorService.ShouldLaunchPlaywrightHeadless());

        Environment.SetEnvironmentVariable("BROWSER_MONITOR_VISIBLE", "true");
        try
        {
            Assert.False(BrowserMonitorService.ShouldLaunchPlaywrightHeadless());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BROWSER_MONITOR_VISIBLE", null);
        }

        Environment.SetEnvironmentVariable("BROWSER_MONITOR_HEADLESS", "false");
        try
        {
            Assert.False(BrowserMonitorService.ShouldLaunchPlaywrightHeadless());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BROWSER_MONITOR_HEADLESS", null);
        }
    }

    [Fact]
    public void FableMonitorPageSelection_IgnoresBlankTabs()
    {
        Assert.True(FableRequestMonitorService.IsBlankBrowserTarget("about:blank"));
        Assert.True(FableRequestMonitorService.IsBlankBrowserTarget("chrome://newtab/"));
        Assert.False(FableRequestMonitorService.IsBlankBrowserTarget("https://app.makeitfable.com/"));
    }

    [Fact]
    public async Task CliCommand_FableRequest_RoutesToFableMonitor()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var projectPath = Path.Combine(repoRoot, "personal-assistant.csproj");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" -- --cli \"fable request\"",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var completed = await Task.WhenAny(Task.Run(() => process.WaitForExit()), Task.Delay(TimeSpan.FromSeconds(90)));
        if (completed != Task.Run(() => process.WaitForExit()))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The CLI Fable request did not finish within 90 seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var output = stdout + Environment.NewLine + stderr;

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Fable", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I could not generate a response", output, StringComparison.OrdinalIgnoreCase);
    }
}
