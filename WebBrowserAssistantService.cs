using Microsoft.Playwright;
using System.Diagnostics;

internal sealed class WebBrowserAssistantService : IAsyncDisposable
{
    private const int MaxContentLength = 6000;

    private readonly bool _headless;
    private readonly int _timeoutMs;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private WebBrowserAssistantService(bool headless, int timeoutMs)
    {
        _headless = headless;
        _timeoutMs = timeoutMs;
    }

    public static WebBrowserAssistantService FromEnvironment()
    {
        var headless = EnvironmentSettings.ReadBool("PLAYWRIGHT_HEADLESS", fallback: true);
        var timeoutSeconds = EnvironmentSettings.ReadInt("PLAYWRIGHT_TIMEOUT_SECONDS", fallback: 30, min: 5, max: 120);
        return new WebBrowserAssistantService(headless, timeoutSeconds * 1000);
    }

    public string GetSetupStatusText()
    {
        return $"Web browser integration is available (headless: {_headless}, timeout: {_timeoutMs / 1000}s). " +
               "Ensure Playwright browsers are installed by running: playwright install chromium";
    }

    /// <summary>Open a URL in the machine's default browser.</summary>
    public Task<string> OpenInDefaultBrowserAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult("No URL provided.");
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        try
        {
            OpenUrlInDefaultBrowser(url);
            return Task.FromResult($"Opened URL in your default browser: {url}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to open URL in the default browser: {ex.Message}");
        }
    }

    /// <summary>Find the top YouTube result for a query and play it in the default browser.</summary>
    public async Task<string> PlayTopYouTubeResultAsync(string query, bool podcastMode = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "No YouTube search query provided.";
        }

        var searchQuery = podcastMode
            ? $"{query} podcast"
            : query;

        var searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(searchQuery)}";

        try
        {
            var browser = await GetBrowserAsync(cancellationToken);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
            });

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(_timeoutMs);

            try
            {
                await page.GotoAsync(searchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _timeoutMs
                });

                await page.WaitForSelectorAsync("ytd-video-renderer a#video-title", new PageWaitForSelectorOptions
                {
                    Timeout = _timeoutMs
                });

                var topResultHref = await page.EvaluateAsync<string>("""
                    (() => {
                        const anchor = document.querySelector('ytd-video-renderer a#video-title');
                        return anchor?.getAttribute('href') || '';
                    })()
                    """);

                if (string.IsNullOrWhiteSpace(topResultHref))
                {
                    OpenUrlInDefaultBrowser(searchUrl);
                    return "I opened YouTube search results, but could not confidently detect the top video to auto-play.";
                }

                var topResultUrl = NormalizeYouTubeUrl(topResultHref);
                var playbackUrl = EnsureAutoplay(topResultUrl);

                OpenUrlInDefaultBrowser(playbackUrl);

                var modeText = podcastMode ? "podcast" : "video";
                return $"Playing top YouTube {modeText} result for '{query}'.\nURL: {playbackUrl}";
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (PlaywrightException ex)
        {
            try
            {
                OpenUrlInDefaultBrowser(searchUrl);
                return $"Browser automation could not pick the top result ({ex.Message}). Opened YouTube search results instead: {searchUrl}";
            }
            catch (Exception fallbackEx)
            {
                return $"Browser automation failed: {ex.Message}. Fallback open also failed: {fallbackEx.Message}";
            }
        }
        catch (Exception ex)
        {
            return $"Failed to play YouTube result: {ex.Message}";
        }
    }

    /// <summary>Navigate to a URL and return the readable text content of the page.</summary>
    public async Task<string> NavigateAndReadAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "No URL provided.";
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        try
        {
            var browser = await GetBrowserAsync(cancellationToken);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
            });

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(_timeoutMs);

            try
            {
                var response = await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _timeoutMs
                });

                var statusCode = response?.Status ?? 0;
                if (statusCode >= 400)
                {
                    return $"Page returned HTTP {statusCode} for URL: {url}";
                }

                var text = await ExtractReadableTextAsync(page);
                var title = await page.TitleAsync();

                return FormatPageResult(url, title, text);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (PlaywrightException ex)
        {
            return $"Browser navigation failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Failed to navigate to page: {ex.Message}";
        }
    }

    /// <summary>Search the web using Bing and return the results as readable text.</summary>
    public async Task<string> SearchWebAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "No search query provided.";
        }

        var searchUrl = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}";
        return await NavigateAndReadAsync(searchUrl, cancellationToken);
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null)
        {
            return _browser;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_browser is not null)
            {
                return _browser;
            }

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _headless
            });

            return _browser;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task<string> ExtractReadableTextAsync(IPage page)
    {
        // Extract meaningful text content, skipping script, style, nav, and footer noise
        var text = await page.EvaluateAsync<string>("""
            (() => {
                const selectors = ['main', 'article', '[role="main"]', '#main-content', '.main-content'];
                for (const sel of selectors) {
                    const el = document.querySelector(sel);
                    if (el) return el.innerText;
                }
                return document.body.innerText;
            })()
            """);

        return text ?? string.Empty;
    }

    private static void OpenUrlInDefaultBrowser(string url)
    {
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static string NormalizeYouTubeUrl(string href)
    {
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }

        if (href.StartsWith('/'))
        {
            return "https://www.youtube.com" + href;
        }

        if (href.StartsWith("watch?", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.youtube.com/" + href;
        }

        return "https://www.youtube.com/" + href.TrimStart('/');
    }

    private static string EnsureAutoplay(string url)
    {
        return url.Contains('?', StringComparison.Ordinal)
            ? $"{url}&autoplay=1"
            : $"{url}?autoplay=1";
    }

    private static string FormatPageResult(string url, string title, string rawText)
    {
        var cleaned = CleanText(rawText);
        var truncated = cleaned.Length > MaxContentLength
            ? cleaned[..MaxContentLength] + $"\n\n[Content truncated at {MaxContentLength} characters]"
            : cleaned;

        return $"URL: {url}\nTitle: {title}\n\n{truncated}";
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Collapse excessive blank lines
        var lines = text.Split('\n');
        var result = new System.Text.StringBuilder();
        var blankCount = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                blankCount++;
                if (blankCount <= 1)
                {
                    result.AppendLine();
                }
            }
            else
            {
                blankCount = 0;
                result.AppendLine(trimmed);
            }
        }

        return result.ToString().Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _initLock.Dispose();
    }
}
