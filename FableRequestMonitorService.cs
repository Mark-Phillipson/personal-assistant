using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal sealed class FableRequestMonitorService
{
    private readonly string _loginUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly int _checkIntervalMinutes;
    private readonly int _startHour;
    private readonly int _endHour;
    private readonly TimeSpan _alertCooldown;
    private readonly TextToSpeechService _textToSpeechService;
    private readonly TickerNotificationService _tickerNotificationService;
    private readonly object _alertLock = new();
    private DateTimeOffset? _lastAlertUtc;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private FableRequestMonitorService(
        string loginUrl,
        string username,
        string password,
        int checkIntervalMinutes,
        int startHour,
        int endHour,
        TextToSpeechService textToSpeechService,
        TickerNotificationService tickerNotificationService)
    {
        _loginUrl = loginUrl;
        _username = username;
        _password = password;
        _checkIntervalMinutes = Math.Clamp(checkIntervalMinutes, 1, 60);
        _startHour = Math.Clamp(startHour, 0, 23);
        _endHour = Math.Clamp(endHour, 0, 23);
        _alertCooldown = TimeSpan.FromMinutes(5);
        _textToSpeechService = textToSpeechService;
        _tickerNotificationService = tickerNotificationService;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_username) &&
        !string.IsNullOrWhiteSpace(_password) &&
        !string.IsNullOrWhiteSpace(_loginUrl);

    public static FableRequestMonitorService FromEnvironment(
        TextToSpeechService textToSpeechService,
        TickerNotificationService tickerNotificationService)
    {
        var loginUrl = EnvironmentSettings.ReadOptionalString("FABLE_LOGIN_URL") ?? "https://app.makeitfable.com/";
        var username = EnvironmentSettings.ReadOptionalString("FABLE_USERNAME");
        var password = EnvironmentSettings.ReadOptionalString("FABLE_PASSWORD");
        var intervalMinutes = NormalizeCheckIntervalMinutes(EnvironmentSettings.ReadOptionalString("FABLE_CHECK_INTERVAL_MINUTES"));
        var startHour = EnvironmentSettings.ReadInt("FABLE_MONITOR_START_HOUR", 6, 0, 23);
        var endHour = EnvironmentSettings.ReadInt("FABLE_MONITOR_END_HOUR", 22, 0, 23);

        return new FableRequestMonitorService(loginUrl, username ?? string.Empty, password ?? string.Empty, intervalMinutes, startHour, endHour, textToSpeechService, tickerNotificationService);
    }

    public static int NormalizeCheckIntervalMinutes(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return 15;
        }

        if (!int.TryParse(rawValue.Trim(), out var parsed))
        {
            return 15;
        }

        if (parsed <= 0)
        {
            return 15;
        }

        return Math.Clamp(parsed, 1, 60);
    }

    public static bool IsWithinMonitoringWindow(DateTime now, int startHour, int endHour)
    {
        var startMinutes = Math.Clamp(startHour, 0, 23) * 60;
        var endMinutes = Math.Clamp(endHour, 0, 23) * 60;
        var currentMinutes = now.Hour * 60 + now.Minute;

        if (startHour <= endHour)
        {
            return currentMinutes >= startMinutes && currentMinutes <= endMinutes;
        }

        return currentMinutes >= startMinutes || currentMinutes <= endMinutes;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return;
        }

        if (ShouldRunImmediateStartupCheck())
        {
            try
            {
                await MonitorOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[fable.monitor] immediate startup check failed: {ex.Message}");
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                if (IsWithinMonitoringWindow(now, _startHour, _endHour))
                {
                    await MonitorOnceAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[fable.monitor] poll failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_checkIntervalMinutes), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public static bool ShouldRunImmediateStartupCheck()
    {
        return EnvironmentSettings.ReadBool("FABLE_RUN_IMMEDIATE_CHECK_ON_START", false);
    }

    public static bool ShouldAutoLaunchEdgeAtStartup()
    {
        return EnvironmentSettings.ReadBool("FABLE_AUTO_LAUNCH_EDGE", true);
    }

    public static async Task EnsureEdgeBrowserDebugSessionIsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!ShouldAutoLaunchEdgeAtStartup())
        {
            return;
        }

        if (IsEdgeBrowserDebugSessionAvailable())
        {
            return;
        }

        var scriptPath = ResolveEdgeLauncherScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            Console.Error.WriteLine("[fable.monitor] Edge helper script not found; skipping startup auto-launch.");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };

        try
        {
            Process.Start(startInfo);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fable.monitor] failed to auto-launch Edge helper: {ex.Message}");
        }
    }

    public static bool IsEdgeDebugPortReachable()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = client.GetAsync("http://127.0.0.1:9223/json/version").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEdgeBrowserDebugSessionAvailable()
    {
        return IsEdgeDebugPortReachable();
    }

    private static string? ResolveEdgeLauncherScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "scripts", "start-fable-edge.ps1"),
            Path.Combine(AppContext.BaseDirectory, "scripts", "start-fable-edge.ps1"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "start-fable-edge.ps1"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "scripts", "start-fable-edge.ps1")
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    public static bool IsFableCheckRequest(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var normalized = prompt.Trim();
        normalized = Regex.Replace(normalized, "^\\s*bob\\s+", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "^\\s*please\\s+", string.Empty, RegexOptions.IgnoreCase);
        normalized = normalized.Trim();

        var patterns = new[]
        {
            @"\bcheck\b.*\bfable\b",
            @"\bfable\b.*\b(?:check|available|requests?|queue|jobs?)\b",
            @"\b(?:using|with|through)\s+(?:the\s+)?fable\s+(?:dashboard|site|page)\b.*\b(?:check|look|see)\b.*\brequests?\b",
            @"\bfable\s+(?:dashboard|site|page)\b.*\b(?:check|look|see)\b.*\brequests?\b",
            @"\b(?:check|find|look|scan|see|review|have|got)\b.*\b(?:available\s+)?(?:fable\s+)?(?:requests?|jobs?)\b",
            @"\b(?:available\s+)?(?:fable\s+)?(?:requests?|jobs?)\b.*\b(?:check|find|look|scan|see|review|have|got)\b",
            @"\bdo\s+i\s+have\s+any\s+fable\s+requests?\b"
        };

        return patterns.Any(pattern => Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase));
    }

    public async Task<FableMonitorCheckResult> MonitorOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new FableMonitorCheckResult(false, 0, false, "Fable monitor is not configured.");
        }

        var browser = await ConnectToEdgeBrowserAsync(cancellationToken);
        var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
        var page = context.Pages.FirstOrDefault(p => p.Url.Contains("makeitfable.com", StringComparison.OrdinalIgnoreCase))
            ?? await context.NewPageAsync();

        try
        {
            var signedIn = await EnsureLoggedInAsync(page, cancellationToken);
            if (!signedIn)
            {
                await TriggerAlertAsync("Fable is not logged in. Please sign in to continue.");
                return new FableMonitorCheckResult(false, 0, false, "Fable login was not completed.");
            }

            var count = await GetAvailableRequestCountAsync(page, cancellationToken);
            if (count > 0)
            {
                var message = $"Fable has {count} available request{(count == 1 ? string.Empty : "s")}.";
                await TriggerAlertAsync(message);
                return new FableMonitorCheckResult(true, count, true, message);
            }

            return new FableMonitorCheckResult(true, 0, false, "Fable is logged in but no requests are currently available.");
        }
        finally
        {
            try
            {
                await page.CloseAsync();
            }
            catch
            {
                // ignore page close errors during polling loops
            }
        }
    }

    private async Task<bool> EnsureLoggedInAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await page.GotoAsync(_loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });

            if (await IsAlreadyLoggedInAsync(page, cancellationToken))
            {
                return true;
            }

            var emailLocator = page.Locator("input[type='email'], input[name='email'], input[id='email']");
            if (await emailLocator.CountAsync() > 0)
            {
                await emailLocator.First.FillAsync(_username, new LocatorFillOptions { Timeout = 15000 });

                var continueButton = page.Locator("button:has-text('Continue'), input[type='submit'], button[type='submit']").First;
                try
                {
                    await continueButton.ClickAsync(new LocatorClickOptions { Timeout = 15000 });
                }
                catch (Exception)
                {
                    await page.ReloadAsync();
                    continue;
                }

                try
                {
                    await page.WaitForSelectorAsync("input[type='password'], input[name='password']", new PageWaitForSelectorOptions { Timeout = 15000 });
                }
                catch (Exception)
                {
                    await page.ReloadAsync();
                    continue;
                }

                var passwordLocator = page.Locator("input[type='password'], input[name='password']").First;
                await passwordLocator.FillAsync(_password, new LocatorFillOptions { Timeout = 15000 });

                var loginButton = page.Locator("button:has-text('Log In'), button:has-text('Login'), input[type='submit'], button[type='submit']").Last;
                try
                {
                    await loginButton.ClickAsync(new LocatorClickOptions { Timeout = 20000 });
                }
                catch (Exception)
                {
                    await page.ReloadAsync();
                    continue;
                }

                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15000 });

                if (await IsAlreadyLoggedInAsync(page, cancellationToken))
                {
                    return true;
                }
            }

            if (attempt < 2)
            {
                await page.ReloadAsync();
            }
        }

        return false;
    }

    private async Task<bool> IsAlreadyLoggedInAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var loginPrompt = page.Locator("text=Log in to Fable, text=Log in, text=Welcome").First;
            if (await loginPrompt.CountAsync() == 0)
            {
                var emailInputCount = await page.Locator("input[type='email'], input[name='email'], input[id='email']").CountAsync();
                var passwordInputCount = await page.Locator("input[type='password'], input[name='password']").CountAsync();
                return emailInputCount == 0 && passwordInputCount == 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<int> GetAvailableRequestCountAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var count = 0;
            var countText = await page.EvaluateAsync<string>(@"
                () => {
                    const selectors = [
                        '[data-testid=""available-requests-count""]',
                        '.available-requests-count',
                        '.request-count',
                        '[data-test=""available-requests""]',
                        'text=Available requests',
                        'li:has-text(""Available requests"")'
                    ];

                    for (const selector of selectors) {
                        const node = document.querySelector(selector);
                        if (node) {
                            const text = (node.textContent || '').replace(/[^0-9]/g, '');
                            if (text) return text;
                        }
                    }

                    const textMatches = Array.from(document.body.querySelectorAll('*')).map(el => el.textContent || '').join(' ');
                    const match = textMatches.match(/Available requests[^0-9]*(\d+)/i);
                    if (match && match[1]) return match[1];
                    return '0';
                }
            ");

            if (!string.IsNullOrWhiteSpace(countText) && int.TryParse(countText.Trim(), out var parsedCount))
            {
                count = parsedCount;
            }

            if (count == 0)
            {
                var cards = await page.Locator("text=Available requests, text=available requests, [data-test*='request'], .request-card").CountAsync();
                if (cards > 0)
                {
                    count = cards;
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private async Task TriggerAlertAsync(string message)
    {
        lock (_alertLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastAlertUtc is not null && now - _lastAlertUtc < _alertCooldown)
            {
                return;
            }

            _lastAlertUtc = now;
        }

        try
        {
            await _tickerNotificationService.EnqueueAndFlushAsync(message, TickerCategory.Critical);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fable.monitor] ticker failed: {ex.Message}");
        }

        try
        {
            await _textToSpeechService.TrySpeakPreviewAsync(message, CancellationToken.None, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fable.monitor] TTS failed: {ex.Message}");
        }
    }

    private async Task<IBrowser> ConnectToEdgeBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null)
        {
            return _browser;
        }

        _playwright ??= await Playwright.CreateAsync();
        var cdpUrl = EnvironmentSettings.ReadOptionalString("FORM_FILL_BROWSER_CDP_URL") ?? "http://127.0.0.1:9223";

        try
        {
            _browser = await _playwright.Chromium.ConnectOverCDPAsync(cdpUrl, new BrowserTypeConnectOverCDPOptions { Timeout = 15000 });
            return _browser;
        }
        catch
        {
            throw new InvalidOperationException(
                "Fable browser monitoring requires Edge remote debugging to be enabled. Launch the dedicated Edge helper from scripts/start-fable-edge.ps1 (or run: msedge --remote-debugging-port=9223 --user-data-dir=\"%LOCALAPPDATA%\\Microsoft\\Edge\\User Data\\FableMonitor\" https://app.makeitfable.com/) and sign in once so the monitor can reuse that session.");
        }
    }
}

internal sealed record FableMonitorCheckResult(bool IsLoggedIn, int AvailableRequestCount, bool HasAvailableRequests, string Message);
