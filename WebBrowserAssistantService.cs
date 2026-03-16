using Microsoft.Playwright;
using System.Diagnostics;

internal sealed class WebBrowserAssistantService : IAsyncDisposable
{
    private const int MaxContentLength = 6000;
    private const string UpworkMessagesUrl = "https://www.upwork.com/ab/messages/";

    private readonly bool _headless;
    private readonly int _timeoutMs;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _upworkSessionContext;
    private IPage? _upworkSessionPage;
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

    public Task<string> GetUpworkSessionStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_headless)
        {
            return Task.FromResult("Upwork browser-assisted flow needs a visible browser. Set PLAYWRIGHT_HEADLESS=false and restart the assistant.");
        }

        if (_upworkSessionPage is null || _upworkSessionPage.IsClosed)
        {
            return Task.FromResult("No active Upwork browser session page. Call upwork_open_messages_portal first, then sign in if prompted.");
        }

        return Task.FromResult($"Upwork session page is open. Current URL: {_upworkSessionPage.Url}");
    }

    public async Task<string> OpenUpworkMessagesPortalAsync(CancellationToken cancellationToken = default)
    {
        if (_headless)
        {
            return "Upwork browser-assisted flow needs a visible browser. Set PLAYWRIGHT_HEADLESS=false and restart the assistant.";
        }

        try
        {
            var page = await EnsureUpworkSessionPageAsync(cancellationToken);
            await page.GotoAsync(UpworkMessagesUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _timeoutMs
            });

            return $"Opened Upwork messages portal in the automation browser: {UpworkMessagesUrl}. If you see a login screen, sign in there and then ask me to read or draft from the current room.";
        }
        catch (Exception ex)
        {
            return $"Failed to open Upwork messages portal: {ex.Message}";
        }
    }

    public async Task<string> ReadUpworkCurrentRoomContextAsync(int latestMessageCount = 8, CancellationToken cancellationToken = default)
    {
        if (_headless)
        {
            return "Upwork browser-assisted flow needs a visible browser. Set PLAYWRIGHT_HEADLESS=false and restart the assistant.";
        }

        var maxMessages = Math.Clamp(latestMessageCount, 1, 30);

        try
        {
            var page = await EnsureUpworkSessionPageAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(page.Url) || !page.Url.Contains("upwork.com", StringComparison.OrdinalIgnoreCase))
            {
                await page.GotoAsync(UpworkMessagesUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _timeoutMs
                });
            }

            var payload = await page.EvaluateAsync<UpworkRoomContextPayload>("""
                (requestedCount) => {
                    const url = window.location.href || '';
                    const roomMatch = url.match(/\/rooms\/([^?/#]+)/i);
                    const roomId = roomMatch ? roomMatch[1] : '';

                    const counterpartCandidates = [
                        '[data-test="room-header"] [data-test="name"]',
                        '[data-test="conversation-title"]',
                        'header h1',
                        'h1'
                    ];

                    let counterpartName = '';
                    for (const selector of counterpartCandidates) {
                        const el = document.querySelector(selector);
                        if (el && el.textContent && el.textContent.trim()) {
                            counterpartName = el.textContent.trim();
                            break;
                        }
                    }

                    const messageNodes = Array.from(document.querySelectorAll('[data-test="message-text"], [data-qa="message-text"], [data-test="message-item"], [data-qa="message-item"]'));
                    const messages = [];

                    for (const node of messageNodes.slice(-requestedCount)) {
                        const rawText = (node.textContent || '').replace(/\s+/g, ' ').trim();
                        if (!rawText) {
                            continue;
                        }

                        let sender = '';
                        const senderNode = node.closest('[data-test="message-item"], [data-qa="message-item"], li, article')?.querySelector('[data-test="sender-name"], [data-qa="sender-name"], strong, h4');
                        if (senderNode && senderNode.textContent) {
                            sender = senderNode.textContent.trim();
                        }

                        messages.push({
                            sender,
                            text: rawText
                        });
                    }

                    const isLoginPage = /\/ab\/account-security\/login/i.test(url) || !!document.querySelector('input[type="password"]');

                    return {
                        url,
                        roomId,
                        counterpartName,
                        isLoginPage,
                        messages
                    };
                }
                """, maxMessages);

            if (payload.IsLoginPage)
            {
                return "Upwork session is currently on a login page. Please complete sign-in in the open automation browser, then ask again.";
            }

            if (string.IsNullOrWhiteSpace(payload.RoomId))
            {
                return "Could not detect an active Upwork message room. Open a specific room in the same browser window, then ask again.";
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Upwork current room context:");
            sb.AppendLine($"URL: {payload.Url}");
            sb.AppendLine($"RoomId: {payload.RoomId}");
            sb.AppendLine($"Counterpart: {(string.IsNullOrWhiteSpace(payload.CounterpartName) ? "(unknown)" : payload.CounterpartName)}");
            sb.AppendLine($"Latest messages captured: {payload.Messages.Count}");
            sb.AppendLine();

            foreach (var message in payload.Messages)
            {
                var who = string.IsNullOrWhiteSpace(message.Sender) ? "Unknown" : message.Sender;
                sb.AppendLine($"- {who}: {message.Text}");
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            return $"Failed to read current Upwork room context: {ex.Message}";
        }
    }

    public async Task<string> ReplyInUpworkCurrentRoomAsync(string replyText, bool sendNow = false, CancellationToken cancellationToken = default)
    {
        if (_headless)
        {
            return "Upwork browser-assisted flow needs a visible browser. Set PLAYWRIGHT_HEADLESS=false and restart the assistant.";
        }

        if (string.IsNullOrWhiteSpace(replyText))
        {
            return "No reply text provided.";
        }

        try
        {
            var page = await EnsureUpworkSessionPageAsync(cancellationToken);
            var mode = sendNow ? "send" : "draft";

            var result = await page.EvaluateAsync<UpworkReplyActionResult>("""
                ({ replyText, sendNow }) => {
                    const visible = (el) => {
                        if (!el) return false;
                        const style = window.getComputedStyle(el);
                        if (!style || style.visibility === 'hidden' || style.display === 'none') return false;
                        const rect = el.getBoundingClientRect();
                        return rect.width > 0 && rect.height > 0;
                    };

                    const candidates = Array.from(document.querySelectorAll('textarea, [contenteditable="true"], [role="textbox"]'));
                    const editor = candidates.find((el) => visible(el));

                    if (!editor) {
                        return {
                            success: false,
                            sent: false,
                            details: 'Could not find a visible reply editor in the current room.'
                        };
                    }

                    editor.focus();

                    if (editor.tagName === 'TEXTAREA') {
                        editor.value = replyText;
                        editor.dispatchEvent(new Event('input', { bubbles: true }));
                        editor.dispatchEvent(new Event('change', { bubbles: true }));
                    } else {
                        editor.textContent = replyText;
                        editor.dispatchEvent(new InputEvent('input', { bubbles: true, data: replyText, inputType: 'insertText' }));
                    }

                    if (!sendNow) {
                        return {
                            success: true,
                            sent: false,
                            details: 'Reply text inserted into the composer as a draft. Review before sending.'
                        };
                    }

                    const sendButtonCandidates = Array.from(document.querySelectorAll('button, [role="button"]'));
                    const sendButton = sendButtonCandidates.find((el) => {
                        if (!visible(el)) return false;
                        const text = (el.textContent || '').trim().toLowerCase();
                        const aria = (el.getAttribute('aria-label') || '').trim().toLowerCase();
                        const dataTest = (el.getAttribute('data-test') || '').trim().toLowerCase();
                        return text === 'send' || aria.includes('send') || dataTest.includes('send');
                    });

                    if (!sendButton) {
                        return {
                            success: true,
                            sent: false,
                            details: 'Draft inserted, but send button was not found. Please send manually in the open browser window.'
                        };
                    }

                    sendButton.click();

                    return {
                        success: true,
                        sent: true,
                        details: 'Reply was sent from the current room.'
                    };
                }
                """, new { replyText = replyText.Trim(), sendNow });

            if (!result.Success)
            {
                return $"Upwork {mode} action failed: {result.Details}";
            }

            return result.Details;
        }
        catch (Exception ex)
        {
            return $"Failed to { (sendNow ? "send" : "draft") } Upwork reply: {ex.Message}";
        }
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

    private async Task<IPage> EnsureUpworkSessionPageAsync(CancellationToken cancellationToken)
    {
        if (_upworkSessionPage is not null && !_upworkSessionPage.IsClosed)
        {
            return _upworkSessionPage;
        }

        var browser = await GetBrowserAsync(cancellationToken);
        _upworkSessionContext ??= await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
        });

        _upworkSessionPage = await _upworkSessionContext.NewPageAsync();
        _upworkSessionPage.SetDefaultTimeout(_timeoutMs);
        return _upworkSessionPage;
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
        if (_upworkSessionPage is not null && !_upworkSessionPage.IsClosed)
        {
            await _upworkSessionPage.CloseAsync();
            _upworkSessionPage = null;
        }

        if (_upworkSessionContext is not null)
        {
            await _upworkSessionContext.CloseAsync();
            _upworkSessionContext = null;
        }

        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _initLock.Dispose();
    }

    private sealed record UpworkRoomContextPayload(
        string Url,
        string RoomId,
        string CounterpartName,
        bool IsLoginPage,
        List<UpworkRoomMessagePayload> Messages);

    private sealed record UpworkRoomMessagePayload(string Sender, string Text);

    private sealed record UpworkReplyActionResult(bool Success, bool Sent, string Details);
}
