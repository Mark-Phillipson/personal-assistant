using System.Diagnostics;

internal sealed class BrowserMonitorService
{
    public sealed record MonitorSiteDefinition(
        string Name,
        string LoginUrl,
        string UsernameEnvironmentVariable,
        string PasswordEnvironmentVariable,
        string? CheckPath = null,
        string? RequestSelector = null,
        string? LoginSelector = null)
    {
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(LoginUrl) &&
            !string.IsNullOrWhiteSpace(UsernameEnvironmentVariable) &&
            !string.IsNullOrWhiteSpace(PasswordEnvironmentVariable);
    }

    public static IReadOnlyList<MonitorSiteDefinition> GetConfiguredMonitorSites()
    {
        var sites = new List<MonitorSiteDefinition>
        {
            new(
                Name: "Fable",
                LoginUrl: EnvironmentSettings.ReadOptionalString("FABLE_LOGIN_URL") ?? "https://app.makeitfable.com/",
                UsernameEnvironmentVariable: "FABLE_USERNAME",
                PasswordEnvironmentVariable: "FABLE_PASSWORD",
                CheckPath: "/",
                RequestSelector: "available-requests-count",
                LoginSelector: "Log in to Fable"
            )
        };

        return sites;
    }

    public static MonitorSiteDefinition GetDefaultSite() =>
        GetConfiguredMonitorSites().FirstOrDefault(site => string.Equals(site.Name, "Fable", StringComparison.OrdinalIgnoreCase))
        ?? GetConfiguredMonitorSites().First();

    public static bool ShouldLaunchPlaywrightHeadless()
    {
        var explicitHeadless = EnvironmentSettings.ReadOptionalString("BROWSER_MONITOR_HEADLESS");
        if (!string.IsNullOrWhiteSpace(explicitHeadless))
        {
            return explicitHeadless.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        var explicitVisible = EnvironmentSettings.ReadOptionalString("BROWSER_MONITOR_VISIBLE");
        if (!string.IsNullOrWhiteSpace(explicitVisible))
        {
            return !explicitVisible.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    public static bool ShouldLaunchHeadfulLoginFallback()
    {
        return EnvironmentSettings.ReadBool("BROWSER_MONITOR_HEADFUL_LOGIN_ON_FAILURE", true);
    }

    public static void LaunchHeadfulLoginFallback(string loginUrl)
    {
        if (string.IsNullOrWhiteSpace(loginUrl))
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "msedge",
                Arguments = $"--new-window \"{loginUrl}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(startInfo);
        }
        catch
        {
            try
            {
                var alternate = new ProcessStartInfo
                {
                    FileName = "microsoft-edge:",
                    UseShellExecute = true
                };

                Process.Start(alternate);
            }
            catch
            {
                // intentionally ignored; leave the user to open the site manually once
            }
        }
    }
}
