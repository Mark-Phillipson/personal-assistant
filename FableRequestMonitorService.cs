using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Playwright;

internal sealed class FableRequestMonitorService
{
    private static readonly object OnDemandLaunchLock = new();
    private static DateTimeOffset? _lastOnDemandLaunchAttemptUtc;

    private readonly string _loginUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly int _checkIntervalMinutes;
    private readonly int _startHour;
    private readonly int _endHour;
    private readonly TimeSpan _alertCooldown;
    private readonly TextToSpeechService _textToSpeechService;
    private readonly TickerNotificationService _tickerNotificationService;
    private readonly bool _useExistingTabOnly;
    private readonly bool _allowPersistentProfileFallback;
    private readonly bool _bringTabToFront;
    private readonly bool _loginTimeoutRecoveryEnabled;
    private readonly object _alertLock = new();
    private DateTimeOffset? _lastAlertUtc;
    private IPlaywright? _playwright;
    private IBrowserContext? _browserContext;
    private IBrowser? _browser;
    private IPage? _monitorPage;
    private readonly string? _availableRequestsSelectorOverride;

    private static void LogInfo(string message)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    }

    private static void LogError(string message)
    {
        Console.Error.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    }

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
        _availableRequestsSelectorOverride = EnvironmentSettings.ReadOptionalString("FABLE_AVAILABLE_REQUESTS_SELECTOR");
        _useExistingTabOnly = EnvironmentSettings.ReadBool("FABLE_USE_EXISTING_TAB_ONLY", true);
        _allowPersistentProfileFallback = EnvironmentSettings.ReadBool("FABLE_ALLOW_PERSISTENT_PROFILE_FALLBACK", false);
        _bringTabToFront = EnvironmentSettings.ReadBool("FABLE_BRING_TAB_TO_FRONT", false);
        _loginTimeoutRecoveryEnabled = EnvironmentSettings.ReadBool("FABLE_LOGIN_TIMEOUT_RECOVERY_ENABLED", true);
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
                LogError($"[fable.monitor] immediate startup check failed: {ex.Message}");
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
                LogError($"[fable.monitor] poll failed: {ex.Message}");
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
        // Default is false so the app no longer opens a remote-debug Edge session on startup.
        // Users can still opt back in explicitly via FABLE_AUTO_LAUNCH_EDGE=true when needed.
        return EnvironmentSettings.ReadBool("FABLE_AUTO_LAUNCH_EDGE", false);
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
            LogError("[fable.monitor] Edge helper script not found; skipping startup auto-launch.");
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
            LogError($"[fable.monitor] failed to auto-launch Edge helper: {ex.Message}");
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

    public static async Task<string> GetAttachDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var cdpUrl = EnvironmentSettings.ReadOptionalString("FABLE_BROWSER_CDP_URL")
            ?? EnvironmentSettings.ReadOptionalString("FORM_FILL_BROWSER_CDP_URL")
            ?? "http://127.0.0.1:9223";

        var normalizedCdpUrl = cdpUrl.TrimEnd('/');
        var useExistingTabOnly = EnvironmentSettings.ReadBool("FABLE_USE_EXISTING_TAB_ONLY", true);
        var allowPersistentFallback = EnvironmentSettings.ReadBool("FABLE_ALLOW_PERSISTENT_PROFILE_FALLBACK", false);

        var lines = new List<string>
        {
            "Fable diagnostics:",
            $"- cdpUrl: {normalizedCdpUrl}",
            $"- useExistingTabOnly: {useExistingTabOnly}",
            $"- allowPersistentProfileFallback: {allowPersistentFallback}"
        };

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            var versionResponse = await client.GetAsync($"{normalizedCdpUrl}/json/version", cancellationToken);
            lines.Add($"- cdpVersionEndpoint: {(versionResponse.IsSuccessStatusCode ? "reachable" : $"unreachable ({(int)versionResponse.StatusCode})")}");

            if (!versionResponse.IsSuccessStatusCode)
            {
                return string.Join("\n", lines);
            }

            var versionJson = await versionResponse.Content.ReadAsStringAsync(cancellationToken);
            using (var versionDoc = JsonDocument.Parse(versionJson))
            {
                if (versionDoc.RootElement.TryGetProperty("Browser", out var browserProp))
                {
                    lines.Add($"- browser: {browserProp.GetString()}");
                }

                if (versionDoc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                {
                    lines.Add($"- webSocketDebuggerUrlPresent: {!string.IsNullOrWhiteSpace(wsProp.GetString())}");
                }
            }

            var targetsResponse = await client.GetAsync($"{normalizedCdpUrl}/json/list", cancellationToken);
            if (!targetsResponse.IsSuccessStatusCode)
            {
                lines.Add($"- targetsEndpoint: unreachable ({(int)targetsResponse.StatusCode})");
                return string.Join("\n", lines);
            }

            var targetsJson = await targetsResponse.Content.ReadAsStringAsync(cancellationToken);
            using var targetsDoc = JsonDocument.Parse(targetsJson);

            if (targetsDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var total = 0;
                var pageTargets = 0;
                var fableTargets = 0;
                var blankTargets = 0;

                foreach (var target in targetsDoc.RootElement.EnumerateArray())
                {
                    total++;

                    var type = target.TryGetProperty("type", out var typeProp)
                        ? typeProp.GetString() ?? string.Empty
                        : string.Empty;

                    var url = target.TryGetProperty("url", out var urlProp)
                        ? urlProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                    {
                        pageTargets++;
                    }

                    if (!string.IsNullOrWhiteSpace(url) && url.Contains("makeitfable.com", StringComparison.OrdinalIgnoreCase))
                    {
                        fableTargets++;
                    }

                    if (string.IsNullOrWhiteSpace(url) || url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
                    {
                        blankTargets++;
                    }
                }

                lines.Add($"- cdpTargetsTotal: {total}");
                lines.Add($"- cdpPageTargets: {pageTargets}");
                lines.Add($"- cdpFableTargets: {fableTargets}");
                lines.Add($"- cdpBlankTargets: {blankTargets}");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"- diagnosticsError: {ex.Message}");
        }

        return string.Join("\n", lines);
    }

    private static bool IsEdgeBrowserDebugSessionAvailable()
    {
        return IsEdgeDebugPortReachable();
    }

    private static bool ShouldAutoLaunchEdgeOnDemand()
    {
        return EnvironmentSettings.ReadBool("FABLE_AUTO_LAUNCH_EDGE_ON_DEMAND", true);
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

    private static async Task<bool> TryLaunchEdgeDebugSessionOnDemandAsync(CancellationToken cancellationToken)
    {
        if (!ShouldAutoLaunchEdgeOnDemand())
        {
            return false;
        }

        if (IsEdgeDebugPortReachable())
        {
            return true;
        }

        var shouldAttemptLaunch = true;
        lock (OnDemandLaunchLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastOnDemandLaunchAttemptUtc is not null && now - _lastOnDemandLaunchAttemptUtc < TimeSpan.FromSeconds(20))
            {
                shouldAttemptLaunch = false;
            }

            if (shouldAttemptLaunch)
            {
                _lastOnDemandLaunchAttemptUtc = now;
            }
        }

        if (!shouldAttemptLaunch)
        {
            return IsEdgeDebugPortReachable();
        }

        var scriptPath = ResolveEdgeLauncherScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            LogError("[fable.monitor] Edge helper script not found for on-demand launch.");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(startInfo);

            var maxWait = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < maxWait)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsEdgeDebugPortReachable())
                {
                    LogInfo("[fable.monitor] on-demand Edge debug session became reachable.");
                    return true;
                }

                await Task.Delay(500, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            LogError($"[fable.monitor] on-demand Edge debug launch failed: {ex.Message}");
        }

        return IsEdgeDebugPortReachable();
    }

    public static bool IsTestCommandArgument(string? arg)
    {
        return !string.IsNullOrWhiteSpace(arg)
            && string.Equals(arg.Trim(), "--test-fable-monitor", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFableCheckRequest(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var normalized = prompt.Trim();
        normalized = Regex.Replace(normalized, @"^\s*bob\s+", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"^\s*please\s+", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"^\s*voice\s+command\s*:\s*", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"^\s*transcript\s*:\s*", string.Empty, RegexOptions.IgnoreCase);
        normalized = normalized.Trim();

        if (normalized.Length == 0)
        {
            return false;
        }

        var lower = normalized.ToLowerInvariant();

        if (Regex.IsMatch(lower, @"\bfable\b"))
        {
            var obviousDefinitions = new[]
            {
                "what is fable",
                "what is a fable",
                "define fable",
                "tell me a fable",
                "tell me a story",
                "write a fable",
                "write fable",
                "fable story",
                "fairy tale",
                "story about fable",
                "fable as a literary device",
                "fable meaning",
                "who is fable",
                "what does fable mean"
            };

            if (obviousDefinitions.Any(pattern => lower.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            // Final hard stop: if the user says anything about Fable in normal monitor language,
            // route it to the Fable monitor before the model can reinterpret it as a generic query.
            if (lower.Contains("fable", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var patterns = new[]
        {
            @"\bcheck\b.*\bfable\b",
            @"\bfable\b.*\b(?:check|available|requests?|queue|jobs?|slots?)\b",
            @"\b(?:check|find|look|scan|see|review|have|got|tell|status)\b.*\b(?:available\s+)?(?:fable\s+)?(?:requests?|jobs?|slots?)\b",
            @"\b(?:available\s+)?(?:fable\s+)?(?:requests?|jobs?|slots?)\b.*\b(?:check|find|look|scan|see|review|have|got|tell|status)\b",
            @"\b(?:using|with|through)\s+(?:the\s+)?fable\s+(?:dashboard|site|page)\b.*\b(?:check|look|see|status|available)\b.*\brequests?\b",
            @"\bfable\s+(?:dashboard|site|page)\b.*\b(?:check|look|see|status|available)\b.*\brequests?\b",
            @"\bdo\s+i\s+have\s+any\s+fable\s+requests?\b",
            @"\bany\s+fable\s+(?:requests?|jobs?|slots?)\b",
            @"\bhow\s+many\s+fable\s+(?:requests?|jobs?|slots?)\b",
            @"\b(?:is\s+)?fable\s+(?:busy|available|open|working)\b",
            @"\bfable\s+(?:available|request|job|slot)\s+(?:now|today|right now)\b"
        };

        return patterns.Any(pattern => Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase));
    }

    public async Task<FableMonitorCheckResult> MonitorOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new FableMonitorCheckResult(false, 0, false, "Fable monitor is not configured.");
        }

        var context = await ConnectToEdgeBrowserAsync(cancellationToken);
        var page = await GetOrCreateMonitorPageAsync(context);

        try
        {
            var signedIn = await EnsureLoggedInAsync(page, cancellationToken);
            if (!signedIn)
            {
                if (!_useExistingTabOnly && BrowserMonitorService.ShouldLaunchHeadfulLoginFallback())
                {
                    BrowserMonitorService.LaunchHeadfulLoginFallback(_loginUrl);
                }

                await TriggerAlertAsync("Fable is not logged in. Please sign in to continue.");
                return new FableMonitorCheckResult(false, 0, false, "Fable login was not completed.");
            }

            var count = await GetAvailableRequestCountAsync(page, cancellationToken);
            if (count < 0)
            {
                if (ShouldFailOnUncertainCount())
                {
                    return new FableMonitorCheckResult(true, 0, false, "Fable is logged in, but I could not reliably read the available requests count from the dashboard.");
                }

                LogInfo("[fable.monitor] could not reliably read available request count; defaulting to zero because strict mode is disabled.");
                count = 0;
            }

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
            if (_bringTabToFront)
            {
                try
                {
                    await page.BringToFrontAsync();
                }
                catch
                {
                    // ignore attempts to focus the Fable tab
                }
            }
        }
    }

    internal static bool IsBlankBrowserTarget(string? url)
    {
        return string.IsNullOrWhiteSpace(url)
            || url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("edge://", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? SelectPreferredFablePageUrl(IEnumerable<string?> candidateUrls)
    {
        var fablePage = candidateUrls
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .FirstOrDefault(url => url.Contains("makeitfable.com", StringComparison.OrdinalIgnoreCase)
                && !IsBlankBrowserTarget(url));

        if (fablePage is not null)
        {
            return fablePage;
        }

        return candidateUrls
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .FirstOrDefault(url => !IsBlankBrowserTarget(url));
    }

    private async Task<IPage> GetOrCreateMonitorPageAsync(IBrowserContext context)
    {
        if (_monitorPage is not null && !_monitorPage.IsClosed)
        {
            var currentUrl = _monitorPage.Url;
            if (IsBlankBrowserTarget(currentUrl) || !currentUrl.Contains("makeitfable.com", StringComparison.OrdinalIgnoreCase))
            {
                var alternateFablePage = context.Pages
                    .FirstOrDefault(page => page != _monitorPage
                        && !IsBlankBrowserTarget(page.Url)
                        && page.Url.Contains("makeitfable.com", StringComparison.OrdinalIgnoreCase));

                if (alternateFablePage is not null)
                {
                    _monitorPage = alternateFablePage;
                    LogInfo($"[fable.monitor] switched to existing Fable page: {_monitorPage.Url}");
                    return _monitorPage;
                }

                if (_useExistingTabOnly)
                {
                    throw new InvalidOperationException("No open Fable tab was found in the attached browser session. Open your logged-in Fable dashboard tab and run the command again.");
                }

                LogInfo($"[fable.monitor] current page was not a usable Fable tab ({currentUrl}); reusing it for Fable navigation.");
                await _monitorPage.GotoAsync(_loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
            }

            LogInfo($"[fable.monitor] reusing existing page: {_monitorPage.Url}");
            return _monitorPage;
        }

        var preferredPage = context.Pages
            .FirstOrDefault(page => !IsBlankBrowserTarget(page.Url)
                && page.Url.Contains("makeitfable.com", StringComparison.OrdinalIgnoreCase));

        if (preferredPage is not null)
        {
            _monitorPage = preferredPage;
            LogInfo($"[fable.monitor] selected existing Fable page from browser session: {_monitorPage.Url}");
            return _monitorPage;
        }

        if (_useExistingTabOnly)
        {
            throw new InvalidOperationException("No open Fable tab was found in the attached browser session. Open your logged-in Fable dashboard tab and run the command again.");
        }

        var firstUsablePage = context.Pages.FirstOrDefault(page => !IsBlankBrowserTarget(page.Url));
        if (firstUsablePage is not null)
        {
            _monitorPage = firstUsablePage;
            LogInfo("[fable.monitor] reusing first non-blank page for Fable navigation.");
            await _monitorPage.GotoAsync(_loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
            return _monitorPage;
        }

        var blankPage = context.Pages.FirstOrDefault(page => IsBlankBrowserTarget(page.Url));
        if (blankPage is not null)
        {
            _monitorPage = blankPage;
            LogInfo($"[fable.monitor] selected blank tab to navigate to Fable login: {_loginUrl}");
            await _monitorPage.GotoAsync(_loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
            return _monitorPage;
        }

        _monitorPage = await context.NewPageAsync();
        LogInfo($"[fable.monitor] no pages existed in context, created one for Fable login: {_loginUrl}");
        await _monitorPage.GotoAsync(_loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
        return _monitorPage;
    }

    private async Task<bool> EnsureLoggedInAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            LogInfo($"[fable.monitor] login attempt {attempt + 1} for {_loginUrl}");

            if (_loginTimeoutRecoveryEnabled)
            {
                await TryRecoverTimedOutSessionAsync(page);
            }

            var alreadyLoggedIn = await IsAlreadyLoggedInAsync(page, cancellationToken);
            LogInfo($"[fable.monitor] already-logged-in check for attempt {attempt + 1}: {alreadyLoggedIn}");
            if (alreadyLoggedIn)
            {
                return true;
            }

            if (_useExistingTabOnly)
            {
                return false;
            }

            await page.GotoAsync(_loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });

            var emailLocator = page.Locator("input[type='email'], input[name='email'], input[id='email']");
            var emailInputCount = await emailLocator.CountAsync();
            LogInfo($"[fable.monitor] email input count on attempt {attempt + 1}: {emailInputCount}");
            if (emailInputCount > 0)
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
                    LogInfo($"[fable.monitor] password field detected on attempt {attempt + 1}.");
                }
                catch (Exception)
                {
                    LogInfo($"[fable.monitor] password field not detected on attempt {attempt + 1}; reloading page.");
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

    private async Task TryRecoverTimedOutSessionAsync(IPage page)
    {
        try
        {
            var timeoutReloginButton = page.Locator(
                "button:has-text('Log in again'), button:has-text('Login again'), button:has-text('Sign in again'), button:has-text('Continue session'), a:has-text('Log in again')").First;

            if (await timeoutReloginButton.CountAsync() > 0)
            {
                LogInfo("[fable.monitor] detected timeout re-login prompt; clicking re-login action.");
                await timeoutReloginButton.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 10000 });
            }

            var passwordLocator = page.Locator("input[type='password'], input[name='password']");
            var emailLocator = page.Locator("input[type='email'], input[name='email'], input[id='email']");

            var passwordCount = await passwordLocator.CountAsync();
            var emailCount = await emailLocator.CountAsync();

            if (passwordCount > 0 && emailCount == 0)
            {
                LogInfo("[fable.monitor] detected password-only re-auth screen; submitting stored password.");
                await passwordLocator.First.FillAsync(_password, new LocatorFillOptions { Timeout = 8000 });

                var submitLocator = page.Locator("button:has-text('Log In'), button:has-text('Login'), button:has-text('Sign in'), button[type='submit'], input[type='submit']").First;
                if (await submitLocator.CountAsync() > 0)
                {
                    await submitLocator.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 12000 });
                }
            }
        }
        catch (Exception ex)
        {
            LogInfo($"[fable.monitor] timeout recovery attempt skipped: {ex.Message}");
        }
    }

    private async Task<bool> IsAlreadyLoggedInAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var loginPrompt = page.Locator("text=Log in to Fable, text=Log in, text=Welcome").First;
            var loginPromptCount = await loginPrompt.CountAsync();
            LogInfo($"[fable.monitor] login prompt count: {loginPromptCount}");
            if (loginPromptCount == 0)
            {
                var emailInputCount = await page.Locator("input[type='email'], input[name='email'], input[id='email']").CountAsync();
                var passwordInputCount = await page.Locator("input[type='password'], input[name='password']").CountAsync();
                LogInfo($"[fable.monitor] detected email inputs: {emailInputCount}, password inputs: {passwordInputCount}");
                return emailInputCount == 0 && passwordInputCount == 0;
            }

            return false;
        }
        catch (Exception ex)
        {
            LogInfo($"[fable.monitor] login detection check threw: {ex.Message}");
            return false;
        }
    }

    private async Task<int> GetAvailableRequestCountAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var sawImplausibleValue = false;
            var selectorCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_availableRequestsSelectorOverride))
            {
                selectorCandidates.Add(_availableRequestsSelectorOverride.Trim());
            }

            selectorCandidates.AddRange(new[]
            {
                "[data-testid='available-requests-count']",
                "[data-test='available-requests-count']",
                "[data-test='available-requests']",
                "[data-qa='available-requests']",
                ".available-requests-count",
                ".request-count"
            });

            foreach (var selector in selectorCandidates)
            {
                var count = await TryReadIntegerFromLocatorAsync(page, selector);
                if (count.HasValue)
                {
                    if (IsPlausibleAvailableRequestCount(count.Value))
                    {
                        LogInfo($"[fable.monitor] extracted available requests from selector '{selector}': {count.Value}");
                        return count.Value;
                    }

                    sawImplausibleValue = true;
                    LogInfo($"[fable.monitor] ignoring implausible count from selector '{selector}': {count.Value}");
                }
            }

            var labelNeighborCount = await TryReadCountNearAvailableRequestsLabelAsync(page);
            if (labelNeighborCount.HasValue)
            {
                if (IsPlausibleAvailableRequestCount(labelNeighborCount.Value))
                {
                    LogInfo($"[fable.monitor] extracted available requests from label neighbor: {labelNeighborCount.Value}");
                    return labelNeighborCount.Value;
                }

                sawImplausibleValue = true;
                LogInfo($"[fable.monitor] ignoring implausible count from label neighbor: {labelNeighborCount.Value}");
            }

            var stateCount = await TryReadCountFromDashboardStateAsync(page);
            if (stateCount.HasValue)
            {
                if (IsPlausibleAvailableRequestCount(stateCount.Value))
                {
                    LogInfo($"[fable.monitor] extracted available requests from dashboard state: {stateCount.Value}");
                    return stateCount.Value;
                }

                sawImplausibleValue = true;
                LogInfo($"[fable.monitor] ignoring implausible count from dashboard state: {stateCount.Value}");
            }

            var nearbyLabelCount = await page.EvaluateAsync<int?>(@"
                () => {
                    const parseIntFromText = (text) => {
                        if (!text) return null;
                        const match = text.match(/(\d+)/);
                        return match ? Number.parseInt(match[1], 10) : null;
                    };

                    const allNodes = Array.from(document.querySelectorAll('*'));
                    for (const node of allNodes) {
                        const text = (node.textContent || '').trim();
                        if (!text || !/available\s+requests?/i.test(text)) {
                            continue;
                        }

                        const ownValue = parseIntFromText(text);
                        if (ownValue !== null) {
                            return ownValue;
                        }

                        const next = node.nextElementSibling;
                        if (next) {
                            const fromNext = parseIntFromText(next.textContent || '');
                            if (fromNext !== null) {
                                return fromNext;
                            }
                        }
                    }

                    return null;
                }
            ");

            if (nearbyLabelCount.HasValue)
            {
                if (IsPlausibleAvailableRequestCount(nearbyLabelCount.Value))
                {
                    LogInfo($"[fable.monitor] extracted available requests from label-adjacent text: {nearbyLabelCount.Value}");
                    return nearbyLabelCount.Value;
                }

                sawImplausibleValue = true;
                LogInfo($"[fable.monitor] ignoring implausible count from label-adjacent text: {nearbyLabelCount.Value}");
            }

            var lineParsedCount = await TryReadCountFromVisibleTextLinesAsync(page);
            if (lineParsedCount.HasValue)
            {
                if (IsPlausibleAvailableRequestCount(lineParsedCount.Value))
                {
                    LogInfo($"[fable.monitor] extracted available requests from visible text lines: {lineParsedCount.Value}");
                    return lineParsedCount.Value;
                }

                sawImplausibleValue = true;
                LogInfo($"[fable.monitor] ignoring implausible count from visible text lines: {lineParsedCount.Value}");
            }

            var cards = await page.Locator("[data-test*='request'], .request-card").CountAsync();
            if (cards > 0)
            {
                LogInfo($"[fable.monitor] falling back to request card count: {cards}");
                return cards;
            }

            if (sawImplausibleValue)
            {
                return -1;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<int?> TryReadCountFromDashboardStateAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<int?>(@"
                () => {
                    const candidates = [];

                    if (typeof window !== 'undefined' && window.__NEXT_DATA__) {
                        try { candidates.push(JSON.stringify(window.__NEXT_DATA__)); } catch {}
                    }

                    const jsonScripts = Array.from(document.querySelectorAll('script[type=""application/json""]'));
                    for (const script of jsonScripts) {
                        const txt = (script.textContent || '').trim();
                        if (txt) candidates.push(txt);
                    }

                    const maxScan = 250000;
                    const patterns = [
                        /availableRequestsCount\s*[:=]\s*(\d{1,6})/i,
                        /availableRequests\s*[:=]\s*(\d{1,6})/i,
                        /requestCount\s*[:=]\s*(\d{1,6})/i,
                        /openRequests\s*[:=]\s*(\d{1,6})/i
                    ];

                    for (const blob of candidates) {
                        const text = (blob || '').slice(0, maxScan);
                        for (const pattern of patterns) {
                            const match = text.match(pattern);
                            if (match && match[1]) {
                                const parsed = Number.parseInt(match[1], 10);
                                if (!Number.isNaN(parsed)) {
                                    return parsed;
                                }
                            }
                        }
                    }

                    return null;
                }
            ");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> TryReadCountFromVisibleTextLinesAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<int?>(@"
                () => {
                    const bodyText = (document.body && document.body.innerText) ? document.body.innerText : '';
                    if (!bodyText) return null;

                    const lines = bodyText
                        .split(/\r?\n/)
                        .map(line => line.trim())
                        .filter(line => line.length > 0)
                        .slice(0, 800);

                    const parseLine = (line) => {
                        const m = line.match(/\b(\d{1,3}(?:,\d{3})*|\d+)\b/);
                        if (!m) return null;
                        const parsed = Number.parseInt(m[1].replace(/,/g, ''), 10);
                        return Number.isNaN(parsed) ? null : parsed;
                    };

                    for (let i = 0; i < lines.length; i++) {
                        const line = lines[i];
                        if (!/available\s+requests?/i.test(line)) continue;

                        const sameLine = parseLine(line);
                        if (sameLine !== null) return sameLine;

                        if (i + 1 < lines.length) {
                            const nextLine = parseLine(lines[i + 1]);
                            if (nextLine !== null) return nextLine;
                        }

                        if (i > 0) {
                            const previousLine = parseLine(lines[i - 1]);
                            if (previousLine !== null) return previousLine;
                        }
                    }

                    return null;
                }
            ");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> TryReadCountNearAvailableRequestsLabelAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<int?>(@"
                () => {
                    const parseNumericText = (text) => {
                        if (!text) return null;
                        const trimmed = text.trim();
                        if (!trimmed) return null;

                        const strict = trimmed.match(/^\d{1,3}(?:,\d{3})*$|^\d+$/);
                        if (strict) {
                            const value = Number.parseInt(trimmed.replace(/,/g, ''), 10);
                            return Number.isNaN(value) ? null : value;
                        }

                        const looser = trimmed.match(/\b(\d{1,3}(?:,\d{3})*|\d+)\b/);
                        if (!looser) return null;
                        const value = Number.parseInt(looser[1].replace(/,/g, ''), 10);
                        return Number.isNaN(value) ? null : value;
                    };

                    const scoreCandidate = (text) => {
                        const trimmed = (text || '').trim();
                        if (!trimmed) return -1;
                        if (/^\d{1,3}(?:,\d{3})*$|^\d+$/.test(trimmed)) return 3;
                        if (/\d/.test(trimmed) && trimmed.length <= 32) return 2;
                        return 0;
                    };

                    const allElements = Array.from(document.querySelectorAll('*'));
                    const labelElements = allElements.filter(el => /available\s+requests?/i.test((el.textContent || '').trim()));

                    for (const labelEl of labelElements) {
                        const candidates = [];

                        if (labelEl.nextElementSibling) candidates.push(labelEl.nextElementSibling);
                        if (labelEl.previousElementSibling) candidates.push(labelEl.previousElementSibling);

                        const parent = labelEl.parentElement;
                        if (parent) {
                            const siblingNodes = Array.from(parent.children).filter(child => child !== labelEl);
                            candidates.push(...siblingNodes);

                            const countLike = parent.querySelectorAll(""[data-testid*='count'], [data-test*='count'], [class*='count'], [aria-label*='count' i]"");
                            candidates.push(...Array.from(countLike));
                        }

                        const ranked = candidates
                            .map(node => ({ node, text: (node.textContent || '').trim() }))
                            .filter(item => !!item.text)
                            .map(item => ({ ...item, score: scoreCandidate(item.text) }))
                            .filter(item => item.score >= 0)
                            .sort((a, b) => b.score - a.score);

                        for (const item of ranked) {
                            const parsed = parseNumericText(item.text);
                            if (parsed !== null) {
                                return parsed;
                            }
                        }
                    }

                    return null;
                }
            ");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPlausibleAvailableRequestCount(int count)
    {
        var maxPlausible = EnvironmentSettings.ReadInt("FABLE_MAX_PLAUSIBLE_AVAILABLE_REQUESTS", 500, 1, 100000);
        return count >= 0 && count <= maxPlausible;
    }

    private static bool ShouldFailOnUncertainCount()
    {
        return EnvironmentSettings.ReadBool("FABLE_FAIL_ON_UNCERTAIN_COUNT", false);
    }

    private static async Task<int?> TryReadIntegerFromLocatorAsync(IPage page, string selector)
    {
        try
        {
            var locator = page.Locator(selector);
            var count = await locator.CountAsync();
            if (count == 0)
            {
                return null;
            }

            var text = (await locator.First.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 3000 }))?.Trim();
            if (TryExtractFirstInteger(text, out var parsed))
            {
                return parsed;
            }

            var inputValue = await locator.First.InputValueAsync(new LocatorInputValueOptions { Timeout = 3000 });
            if (TryExtractFirstInteger(inputValue, out parsed))
            {
                return parsed;
            }

            var ariaLabel = await locator.First.GetAttributeAsync("aria-label");
            if (TryExtractFirstInteger(ariaLabel, out parsed))
            {
                return parsed;
            }

            var title = await locator.First.GetAttributeAsync("title");
            if (TryExtractFirstInteger(title, out parsed))
            {
                return parsed;
            }

            var dataValue = await locator.First.GetAttributeAsync("data-value");
            if (TryExtractFirstInteger(dataValue, out parsed))
            {
                return parsed;
            }

            var dataCount = await locator.First.GetAttributeAsync("data-count");
            if (TryExtractFirstInteger(dataCount, out parsed))
            {
                return parsed;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool TryExtractFirstInteger(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Regex.Match(text, "\\d+");
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Value, out value);
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
            LogError($"[fable.monitor] ticker failed: {ex.Message}");
        }

        try
        {
            await _textToSpeechService.TrySpeakPreviewAsync(message, CancellationToken.None, true);
        }
        catch (Exception ex)
        {
            LogError($"[fable.monitor] TTS failed: {ex.Message}");
        }
    }

    private async Task<IBrowserContext> ConnectToEdgeBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browserContext is not null)
        {
            return _browserContext;
        }

        _playwright ??= await Playwright.CreateAsync();

        // Prefer attaching to the user's existing browser session first.
        // This avoids the "logged in on visible tab but not in automation tab" split-brain.
        var cdpUrl = EnvironmentSettings.ReadOptionalString("FABLE_BROWSER_CDP_URL")
            ?? EnvironmentSettings.ReadOptionalString("FORM_FILL_BROWSER_CDP_URL")
            ?? "http://127.0.0.1:9223";

        try
        {
            _browser = await _playwright.Chromium.ConnectOverCDPAsync(cdpUrl, new BrowserTypeConnectOverCDPOptions { Timeout = 15000 });
            _browserContext = SelectBestContext(_browser.Contexts) ?? _browser.Contexts.FirstOrDefault();
            if (_browserContext is null)
            {
                throw new InvalidOperationException("Attached to Edge via CDP, but no browser context was available to reuse.");
            }
            LogInfo($"[fable.monitor] attached to shared browser session via CDP at {cdpUrl}.");
            return _browserContext;
        }
        catch (Exception ex)
        {
            LogError($"[fable.monitor] CDP attach failed at {cdpUrl}; falling back to persistent profile. {ex.Message}");
        }

        var launchedOnDemand = await TryLaunchEdgeDebugSessionOnDemandAsync(cancellationToken);
        if (launchedOnDemand)
        {
            try
            {
                _browser = await _playwright.Chromium.ConnectOverCDPAsync(cdpUrl, new BrowserTypeConnectOverCDPOptions { Timeout = 15000 });
                _browserContext = SelectBestContext(_browser.Contexts) ?? _browser.Contexts.FirstOrDefault();
                if (_browserContext is null)
                {
                    throw new InvalidOperationException("Attached to Edge via CDP after on-demand launch, but no browser context was available to reuse.");
                }

                LogInfo($"[fable.monitor] attached to shared browser session after on-demand launch at {cdpUrl}.");
                return _browserContext;
            }
            catch (Exception ex)
            {
                LogError($"[fable.monitor] CDP re-attach failed after on-demand launch at {cdpUrl}: {ex.Message}");
            }
        }

        if (!_allowPersistentProfileFallback)
        {
            throw new InvalidOperationException(
                $"Fable check could not attach to the existing browser debug session at {cdpUrl}. " +
                "No new browser window was opened. Keep the Playwright Edge session running with remote debugging and your logged-in Fable tab open, then try again.");
        }

        if (_useExistingTabOnly)
        {
            throw new InvalidOperationException(
                "Fable browser monitoring requires an attachable Edge remote-debug session. Start Edge with remote debugging and keep your logged-in Fable tab open, then run the command again.");
        }

        var userDataDir = EnvironmentSettings.ReadOptionalString("BROWSER_MONITOR_PROFILE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "PersonalAssistant-FableMonitor");

        try
        {
            _browserContext = await _playwright.Chromium.LaunchPersistentContextAsync(
                userDataDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Channel = "msedge",
                    Headless = BrowserMonitorService.ShouldLaunchPlaywrightHeadless(),
                    IgnoreDefaultArgs = new[] { "--enable-automation" }
                });

            return _browserContext;
        }
        catch (Exception ex)
        {
            LogError($"[fable.monitor] headless persistent browser launch failed: {ex.Message}");
        }

        throw new InvalidOperationException(
            "Fable browser monitoring requires a usable Edge session. The app tried CDP attach first and then a persistent profile; neither was available. Launch Edge with remote debugging and sign in once, or configure a valid persistent profile for the monitor.");
    }

    private static IBrowserContext? SelectBestContext(IReadOnlyList<IBrowserContext> contexts)
    {
        if (contexts.Count == 0)
        {
            return null;
        }

        var contextWithFableTab = contexts.FirstOrDefault(context =>
            context.Pages.Any(page =>
                !IsBlankBrowserTarget(page.Url) &&
                page.Url.Contains("makeitfable.com", StringComparison.OrdinalIgnoreCase)));

        return contextWithFableTab ?? contexts.FirstOrDefault();
    }
}

internal sealed record FableMonitorCheckResult(bool IsLoggedIn, int AvailableRequestCount, bool HasAvailableRequests, string Message);
