using Microsoft.Playwright;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Text.Json;

internal sealed class WebBrowserAssistantService : IAsyncDisposable
{
    private const int MaxContentLength = 6000;
    private const int MinimumPodcastCandidateScore = 45;
    private const int MinimumSpotifyAlbumCandidateScore = 85;
    private const string UpworkMessagesUrl = "https://www.upwork.com/ab/messages/";
    private const string DefaultChromeCdpUrl = "http://127.0.0.1:9222";
    private static readonly string UpworkLogFile = "logs/upwork-browser.log";
    private static readonly TimeSpan UpworkCdpRetryCooldown = TimeSpan.FromSeconds(10);
    private static readonly string[] PodcastHintKeywords = ["podcast", "episode", "ep.", "ep ", "interview", "show", "full episode"];
    private static readonly string[] NonPodcastKeywords = ["remix", "speed up", "sped up", "song", "lyrics", "lyric", "instrumental", "playlist", "mix", "beats", "official audio", "official music video", "karaoke"];
    private static readonly HashSet<string> QueryStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "for", "to", "from", "of", "latest", "new", "podcast", "episode", "show"
    };

    private readonly bool _headless;
    private readonly int _timeoutMs;
    private readonly string? _upworkChromeCdpUrl;
    private readonly ClipboardAssistantService? _clipboardService;
    private readonly string? _formBrowserCdpUrl;
    private readonly string _formBrowserKey;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowser? _upworkBrowser;
    private IBrowserContext? _upworkSessionContext;
    private IPage? _upworkSessionPage;
    private bool _upworkUsingCdp;
    private DateTimeOffset? _upworkLastCdpAttemptUtc;
    private string? _upworkCdpLastError;
    private IBrowser? _formBrowser;
    private bool _formBrowserIsCdp;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private WebBrowserAssistantService(bool headless, int timeoutMs, string? upworkChromeCdpUrl, ClipboardAssistantService? clipboardService, string? formBrowserCdpUrl, string formBrowserKey)
    {
        _headless = headless;
        _timeoutMs = timeoutMs;
        _upworkChromeCdpUrl = upworkChromeCdpUrl;
        _clipboardService = clipboardService;
        _formBrowserCdpUrl = formBrowserCdpUrl;
        _formBrowserKey = formBrowserKey;
    }

    private static string GetChromeExecutablePath()
    {
        // Default Windows path; can be extended for Mac/Linux
        var winPath = @"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
        if (System.IO.File.Exists(winPath)) return winPath;
        var winPathX86 = @"C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe";
        if (System.IO.File.Exists(winPathX86)) return winPathX86;
        throw new InvalidOperationException("Could not find Google Chrome executable. Please install Chrome or set UPWORK_CHROME_CDP_URL to a running instance.");
    }

    private static string GetEdgeExecutablePath()
    {
        var paths = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        };
        foreach (var path in paths)
            if (System.IO.File.Exists(path)) return path;
        throw new InvalidOperationException("Could not find Microsoft Edge executable. Please install Edge or set FORM_FILL_BROWSER_CDP_URL to a running Edge instance.");
    }

    private string GetConfiguredFormBrowserPath()
    {
        return _formBrowserKey.ToLowerInvariant() switch
        {
            "msedge" or "edge" => GetEdgeExecutablePath(),
            "chrome" => GetChromeExecutablePath(),
            _ => GetEdgeExecutablePath()
        };
    }

    public static WebBrowserAssistantService FromEnvironment(ClipboardAssistantService? clipboardService = null)
    {
        var headless = EnvironmentSettings.ReadBool("PLAYWRIGHT_HEADLESS", fallback: true);
        var timeoutSeconds = EnvironmentSettings.ReadInt("PLAYWRIGHT_TIMEOUT_SECONDS", fallback: 30, min: 5, max: 120);
        var upworkChromeCdpUrl = EnvironmentSettings.ReadOptionalString("UPWORK_CHROME_CDP_URL");
        var formBrowserCdpUrl = EnvironmentSettings.ReadOptionalString("FORM_FILL_BROWSER_CDP_URL");
        var formBrowserKey = EnvironmentSettings.ReadString("FORM_FILL_BROWSER", "msedge");
        return new WebBrowserAssistantService(headless, timeoutSeconds * 1000, upworkChromeCdpUrl, clipboardService, formBrowserCdpUrl, formBrowserKey);
    }

    public string GetSetupStatusText()
    {
        return $"Web browser integration is available (headless: {_headless}, timeout: {_timeoutMs / 1000}s). " +
               "Ensure Playwright browsers are installed by running: playwright install chromium";
    }

    public Task<string> GetUpworkSessionStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_upworkSessionPage is null || _upworkSessionPage.IsClosed)
        {
            var cdpHint = BuildCdpHint();
            return Task.FromResult("No active Upwork browser session page. Call upwork_open_messages_portal first, or take a screenshot of your Upwork message thread and send it here to draft a reply directly. " + cdpHint);
        }

        var sessionType = _upworkUsingCdp ? "shared Chrome session (CDP)" : "automation browser session";
        var errorSuffix = string.IsNullOrWhiteSpace(_upworkCdpLastError)
            ? string.Empty
            : $" Last CDP error: {_upworkCdpLastError}";
        return Task.FromResult($"Upwork session page is open in {sessionType}. Current URL: {_upworkSessionPage.Url}.{errorSuffix}");
    }

    public async Task<string> OpenUpworkMessagesPortalAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await EnsureUpworkSessionPageAsync(cancellationToken);
            await page.GotoAsync(UpworkMessagesUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _timeoutMs
            });

            if (_upworkUsingCdp)
            {
                return $"Opened Upwork messages portal in your shared Chrome session: {UpworkMessagesUrl}.";
            }

            var cdpHint = BuildCdpHint();
            return $"Opened Upwork messages portal in the automation browser (not your logged-in Chrome session). {cdpHint}\n\nAlternatively, take a screenshot of any Upwork message thread in your browser and send it here — I'll read it and draft a reply for you directly.";
        }
        catch (Exception ex)
        {
            return $"Failed to open Upwork messages portal: {ex.Message}";
        }
    }

    public async Task<string> ReadUpworkCurrentRoomContextAsync(int latestMessageCount = 8, CancellationToken cancellationToken = default)
    {
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
        if (string.IsNullOrWhiteSpace(replyText))
        {
            return "No reply text provided.";
        }

        try
        {
            var page = await EnsureUpworkSessionPageAsync(cancellationToken);
            var mode = sendNow ? "send" : "draft";
            var trimmedReply = replyText.Trim();

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
                """, new { replyText = trimmedReply, sendNow });

            if (!result.Success)
            {
                return $"Upwork {mode} action failed: {result.Details}";
            }

            if (!sendNow)
            {
                var clipboardMessage = await TryCopyDraftToClipboardAsync(trimmedReply, cancellationToken);
                if (!string.IsNullOrWhiteSpace(clipboardMessage))
                {
                    return $"{result.Details}\n{clipboardMessage}";
                }
            }

            return result.Details;
        }
        catch (Exception ex)
        {
            return $"Failed to { (sendNow ? "send" : "draft") } Upwork reply: {ex.Message}";
        }
    }

    private async Task<string?> TryCopyDraftToClipboardAsync(string draftText, CancellationToken cancellationToken)
    {
        if (_clipboardService is null || !_clipboardService.IsSupported)
        {
            return null;
        }

        var result = await _clipboardService.SetClipboardTextAsync(draftText, cancellationToken);
        if (result.Success)
        {
            return "The draft was also copied to your clipboard.";
        }

        return $"Draft created, but clipboard copy failed: {result.Message}";
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
            ? BuildPodcastSearchQuery(query)
            : query;

        var searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(searchQuery)}";
        var fallbackUrl = podcastMode
            ? $"https://music.youtube.com/search?q={Uri.EscapeDataString(searchQuery)}"
            : searchUrl;

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

                var candidates = await page.EvaluateAsync<List<YouTubeSearchCandidate>>("""
                    (() => {
                        const renderers = Array.from(document.querySelectorAll('ytd-video-renderer')).slice(0, 8);
                        return renderers.map(renderer => {
                            const anchor = renderer.querySelector('a#video-title');
                            const metadata = Array.from(renderer.querySelectorAll('#metadata-line span'))
                                .map(node => (node.textContent || '').trim())
                                .filter(Boolean)
                                .join(' | ');
                            return {
                                href: anchor?.getAttribute('href') || '',
                                title: (anchor?.textContent || '').trim(),
                                metadata,
                                ariaLabel: anchor?.getAttribute('aria-label') || ''
                            };
                        });
                    })()
                    """);

                var selectedCandidate = SelectBestYouTubeCandidate(candidates, query, podcastMode);

                if (selectedCandidate == null || string.IsNullOrWhiteSpace(selectedCandidate.Href))
                {
                    OpenUrlInDefaultBrowser(fallbackUrl);
                    return podcastMode
                        ? "I opened YouTube Music search results because I could not confidently identify a podcast episode to auto-play."
                        : "I opened YouTube search results, but could not confidently detect the top video to auto-play.";
                }

                var topResultUrl = NormalizeYouTubeUrl(selectedCandidate.Href);
                var youtubePlaybackUrl = EnsureAutoplay(topResultUrl);
                var useYouTubeMusic = podcastMode && ShouldUseYouTubeMusicForPodcastCandidate(selectedCandidate);
                var playbackUrl = useYouTubeMusic
                    ? TryConvertToYouTubeMusicUrl(youtubePlaybackUrl) ?? youtubePlaybackUrl
                    : youtubePlaybackUrl;

                OpenUrlInDefaultBrowser(playbackUrl);

                var modeText = podcastMode ? "podcast" : "video";
                var serviceName = podcastMode && IsYouTubeMusicUrl(playbackUrl)
                    ? "YouTube Music"
                    : "YouTube";
                return $"Playing top {serviceName} {modeText} result for '{query}'.\nTitle: {selectedCandidate.Title}\nURL: {playbackUrl}";
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
                OpenUrlInDefaultBrowser(fallbackUrl);
                return podcastMode
                    ? $"Browser automation could not pick the top result ({ex.Message}). Opened YouTube Music search results instead: {fallbackUrl}"
                    : $"Browser automation could not pick the top result ({ex.Message}). Opened YouTube search results instead: {fallbackUrl}";
            }
            catch (Exception fallbackEx)
            {
                return $"Browser automation failed: {ex.Message}. Fallback open also failed: {fallbackEx.Message}";
            }
        }
        catch (Exception ex)
        {
            try
            {
                OpenUrlInDefaultBrowser(fallbackUrl);
                return podcastMode
                    ? $"YouTube automation timed out while selecting a video ({ex.Message}). Opened YouTube Music search results instead: {fallbackUrl}"
                    : $"YouTube automation timed out while selecting a video ({ex.Message}). Opened YouTube search results instead: {fallbackUrl}";
            }
            catch
            {
                // If fallback open also fails, return the original error.
            }

            return $"Failed to play YouTube result: {ex.Message}";
        }
    }

    /// <summary>Play the latest uploaded video from a YouTube channel URL using the public channel feed.</summary>
    public async Task<string> PlayLatestFromYouTubeChannelAsync(string channelUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelUrl))
        {
            return "No YouTube channel URL provided.";
        }

        if (!Uri.TryCreate(channelUrl, UriKind.Absolute, out var parsedChannelUri))
        {
            return "Invalid YouTube channel URL.";
        }

        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Math.Clamp(_timeoutMs / 1000, 8, 45))
            };

            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            var channelId = await TryResolveYouTubeChannelIdAsync(httpClient, parsedChannelUri, cancellationToken);
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return await OpenInDefaultBrowserAsync(channelUrl, cancellationToken);
            }

            var feedUrl = $"https://www.youtube.com/feeds/videos.xml?channel_id={Uri.EscapeDataString(channelId)}";
            var feedXml = await httpClient.GetStringAsync(feedUrl, cancellationToken);

            var document = XDocument.Parse(feedXml);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            XNamespace yt = "http://www.youtube.com/xml/schemas/2015";

            var latestEntry = document.Root?
                .Elements(atom + "entry")
                .FirstOrDefault();
            var latestVideoId = latestEntry?.Element(yt + "videoId")?.Value?.Trim();
            var latestTitle = latestEntry?.Element(atom + "title")?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(latestVideoId))
            {
                return await OpenInDefaultBrowserAsync(channelUrl, cancellationToken);
            }

            var playbackUrl = EnsureAutoplay($"https://www.youtube.com/watch?v={latestVideoId}");
            OpenUrlInDefaultBrowser(playbackUrl);

            var safeTitle = string.IsNullOrWhiteSpace(latestTitle) ? "Latest upload" : latestTitle;
            return $"Playing latest episode from channel '{channelUrl}'.\nTitle: {safeTitle}\nURL: {playbackUrl}";
        }
        catch (Exception ex)
        {
            // Fall back to opening the channel if feed lookup fails.
            var openResult = await OpenInDefaultBrowserAsync(channelUrl, cancellationToken);
            return $"Could not resolve latest channel upload automatically ({ex.Message}). {openResult}";
        }
    }

    public Task<string> OpenSpotifySearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult("No Spotify search query provided.");
        }

        var searchUrl = $"https://open.spotify.com/search/{Uri.EscapeDataString(query.Trim())}";
        return OpenInDefaultBrowserAsync(searchUrl, cancellationToken);
    }

    public Task<string> PlaySpotifyFocusMusicAsync(string? activity = null, CancellationToken cancellationToken = default)
    {
        var trimmedActivity = activity?.Trim();
        var focusQuery = BuildSpotifyFocusQuery(trimmedActivity);
        return OpenSpotifySearchAsync(focusQuery, cancellationToken);
    }

    public Task<string> PlayYouTubeMusicFocusAsync(string? activity = null, CancellationToken cancellationToken = default)
    {
        var trimmedActivity = activity?.Trim();
        var focusQuery = BuildSpotifyFocusQuery(trimmedActivity);
        var youtubeUrl = $"https://music.youtube.com/search?q={Uri.EscapeDataString(focusQuery)}";
        return OpenInDefaultBrowserAsync(youtubeUrl, cancellationToken);
    }

    public async Task<string> PlayFocusMusicAsync(string? activity = null, string? preferredService = null, CancellationToken cancellationToken = default)
    {
        preferredService = preferredService?.Trim().ToLowerInvariant();
        var trimmedActivity = activity?.Trim();

        if (preferredService == "youtube" || preferredService == "youtube music")
        {
            var youtubeResult = await PlayYouTubeMusicFocusAsync(trimmedActivity, cancellationToken);
            return youtubeResult;
        }

        try
        {
            var spotifyResult = await PlaySpotifyFocusMusicAsync(trimmedActivity, cancellationToken);
            return spotifyResult + "\n\nTip: If Spotify isn't logged in or you prefer YouTube Music, try 'play focus music on YouTube Music' instead.";
        }
        catch
        {
            var youtubeResult = await PlayYouTubeMusicFocusAsync(trimmedActivity, cancellationToken);
            return $"Spotify is unavailable, so opening YouTube Music focus music instead.\n{youtubeResult}";
        }
    }

    public async Task<string> PlayLatestSpotifyAlbumAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return "No artist name provided.";
        }

        var trimmedArtistName = artistName.Trim();
        var fallbackUrl = $"https://open.spotify.com/search/{Uri.EscapeDataString($"{trimmedArtistName} latest album")}";
        var searchQuery = $"site:open.spotify.com/album {trimmedArtistName} latest album Spotify";
        var searchUrl = $"https://www.bing.com/search?q={Uri.EscapeDataString(searchQuery)}";

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

                await page.WaitForSelectorAsync("li.b_algo h2 a, a[href*='open.spotify.com/album/']", new PageWaitForSelectorOptions
                {
                    Timeout = _timeoutMs
                });

                var candidates = await page.EvaluateAsync<List<WebSearchCandidate>>("""
                    (() => {
                        const resultNodes = Array.from(document.querySelectorAll('li.b_algo')).slice(0, 10);
                        return resultNodes.map(node => {
                            const anchor = node.querySelector('h2 a, a[href]');
                            const snippetNode = node.querySelector('.b_caption p, .b_snippet, p');
                            return {
                                href: anchor?.href || '',
                                title: (anchor?.textContent || '').trim(),
                                snippet: (snippetNode?.textContent || '').trim()
                            };
                        }).filter(result => result.href);
                    })()
                    """);

                var selectedCandidate = SelectBestSpotifyAlbumCandidate(candidates, trimmedArtistName);
                if (selectedCandidate is null || string.IsNullOrWhiteSpace(selectedCandidate.Href))
                {
                    OpenUrlInDefaultBrowser(fallbackUrl);
                    return $"I could not confidently identify the latest Spotify album for '{trimmedArtistName}', so I opened Spotify search instead: {fallbackUrl}";
                }

                OpenUrlInDefaultBrowser(selectedCandidate.Href);
                return $"Opened the latest Spotify album I found for '{trimmedArtistName}'.\nAlbum: {selectedCandidate.Title}\nURL: {selectedCandidate.Href}\nNote: this uses browser search/open, so playback still depends on your logged-in Spotify session and free-account limits.";
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
                OpenUrlInDefaultBrowser(fallbackUrl);
                return $"Spotify album lookup could not confirm the latest result ({ex.Message}). Opened Spotify search instead: {fallbackUrl}";
            }
            catch (Exception fallbackEx)
            {
                return $"Spotify album lookup failed: {ex.Message}. Fallback open also failed: {fallbackEx.Message}";
            }
        }
        catch (Exception ex)
        {
            try
            {
                OpenUrlInDefaultBrowser(fallbackUrl);
                return $"Spotify album lookup failed ({ex.Message}). Opened Spotify search instead: {fallbackUrl}";
            }
            catch
            {
                // If fallback open also fails, return the original error.
            }

            return $"Failed to open latest Spotify album: {ex.Message}";
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

    /// <summary>
    /// <summary>
    /// Navigate to a URL and fill in named form fields. Never submits or closes the tab —
    /// the user reviews and submits manually.
    /// <paramref name="formFieldsJson"/> should be a JSON object mapping field name/id to value,
    /// e.g. {"firstName":"Carla","lastName":"Schmid"}.
    /// </summary>
    public async Task<string> FillWebFormAsync(string url, string formFieldsJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "No URL provided.";
        if (string.IsNullOrWhiteSpace(formFieldsJson))
            return "No form fields provided.";

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        Dictionary<string, string> fields;
        try
        {
            fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(formFieldsJson)
                     ?? throw new InvalidOperationException("Parsed result was null.");
        }
        catch (Exception ex)
        {
            return $"Invalid formFieldsJson: {ex.Message}";
        }

        try
        {
            var browser = await GetFormBrowserAsync(cancellationToken);
            var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();

            // Find the tab already showing the target URL — never create new tabs or navigate existing ones.
            var page = FindPageForUrl(context, url);
            if (page is null)
                return $"No open tab is currently showing {url}. Please open that page in your Edge browser and try again.";

            page.SetDefaultTimeout(_timeoutMs);

            var filled = new List<string>();
            var skipped = new List<string>();

            foreach (var (fieldName, value) in fields)
            {
                var selector = $"[name='{fieldName}'], #{fieldName}";
                var element = page.Locator(selector).First;
                try
                {
                    await element.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
                    var tagName = await element.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
                    if (tagName == "select")
                        await element.SelectOptionAsync(value);
                    else
                        await element.FillAsync(value);
                    filled.Add(fieldName);
                }
                catch
                {
                    skipped.Add(fieldName);
                }
            }

            var summary = $"Filled {filled.Count} field(s): {string.Join(", ", filled)}.";
            if (skipped.Count > 0)
                summary += $" Could not locate {skipped.Count} field(s): {string.Join(", ", skipped)}.";
            summary += " The tab is still open — please review and submit manually.";
            return summary;
        }
        catch (PlaywrightException ex)
        {
            return $"Browser form fill failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Failed to fill form: {ex.Message}";
        }
    }

    /// <summary>
    /// Navigate to a URL in the user's actual browser (Edge by default) and return the form fields
    /// using the accessibility snapshot. Password fields are excluded and flagged in warnings.
    /// </summary>
    public async Task<string> ReadWebFormStructureAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "No URL provided.";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        try
        {
            var browser = await GetFormBrowserAsync(cancellationToken);
            var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();

            var page = FindPageForUrl(context, url);
            if (page is null)
                return $"No open tab is currently showing {url}. Please open that page in your Edge browser and try again.";

            page.SetDefaultTimeout(_timeoutMs);

#pragma warning disable CS0612
                var snapshot = await page.Accessibility.SnapshotAsync();
#pragma warning restore CS0612
                if (snapshot is null)
                    return "No accessibility snapshot available. The page may be blank or not interactive.";

                var fields = new List<System.Text.Json.JsonElement>();
                var warnings = new List<string>();
                FlattenFormControls(snapshot.Value, fields, warnings);

                if (fields.Count == 0)
                {
                    var msg = "No form fields detected on this page.";
                    if (warnings.Count > 0)
                        msg += " Warnings: " + string.Join("; ", warnings);
                    return msg;
                }

                var result = new System.Text.Json.Nodes.JsonObject
                {
                    ["url"] = url,
                    ["fieldCount"] = fields.Count,
                    ["fields"] = new System.Text.Json.Nodes.JsonArray(fields.Select(f =>
                        System.Text.Json.Nodes.JsonNode.Parse(f.GetRawText())!).ToArray()),
                };
                if (warnings.Count > 0)
                    result["warnings"] = new System.Text.Json.Nodes.JsonArray(warnings.Select(w =>
                        System.Text.Json.Nodes.JsonValue.Create(w)!).Cast<System.Text.Json.Nodes.JsonNode>().ToArray());

                return result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return $"Failed to read form structure: {ex.Message}";
        }
    }

    /// <summary>
    /// Take a screenshot of the given URL using the user's actual browser and save it to the logs folder.
    /// Returns the file path of the saved screenshot.
    /// </summary>
    public async Task<string> TakePageScreenshotAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "No URL provided.";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        try
        {
            var browser = await GetFormBrowserAsync(cancellationToken);
            var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();

            var page = FindPageForUrl(context, url);
            if (page is null)
                return $"No open tab is showing {url}. Please open that page in Edge first.";

            System.IO.Directory.CreateDirectory("logs");
            var screenshotPath = $"logs/form-screenshot-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.png";
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath, FullPage = false });

            return screenshotPath;
        }
        catch (Exception ex)
        {
            return $"Screenshot failed: {ex.Message}";
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

    /// <summary>
    /// Get (or lazily create) a browser connected to the user's actual running browser via CDP.
    /// Defaults to Microsoft Edge on port 9223. If no running instance is found, Edge is launched
    /// with --remote-debugging-port=9223 so the user can log in manually.
    /// </summary>
    /// <summary>
    /// Find an open page whose URL matches the given URL (ignoring trailing slash differences).
    /// Returns null if no match — callers should then ask the user to open the page rather than
    /// navigating or creating new tabs.
    /// </summary>
    private static IPage? FindPageForUrl(IBrowserContext context, string url)
    {
        var normalised = url.TrimEnd('/');
        return context.Pages.FirstOrDefault(p =>
            p.Url.TrimEnd('/').StartsWith(normalised, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetRealEdgeProfileDir() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data");

    /// <summary>
    /// Launches Edge with --remote-debugging-port=9223 using your real Edge profile.
    /// If Edge is already running WITHOUT the debug port, it closes existing Edge windows
    /// first (to free the profile lock), then relaunches with the debug port.
    /// Returns once the CDP endpoint is confirmed listening.
    /// </summary>
    public string LaunchFormBrowser()
    {
        var cdpUrl = string.IsNullOrWhiteSpace(_formBrowserCdpUrl)
            ? "http://127.0.0.1:9223"
            : _formBrowserCdpUrl;

        // If CDP port is already responding, nothing to do
        if (IsCdpPortUp(cdpUrl))
            return $"✅ Edge is already running with remote debugging at {cdpUrl}. Ready to fill forms.";

        var exePath = GetConfiguredFormBrowserPath();
        var profileDir = GetRealEdgeProfileDir();

        // Kill any existing main Edge processes (those WITHOUT --type= are the browser host).
        // Subprocesses (renderer, gpu, utility) will exit automatically when the parent dies.
        var mainEdgeProcs = System.Diagnostics.Process.GetProcessesByName("msedge")
            .Where(p =>
            {
                try
                {
                    var cmdLine = p.MainModule?.FileName; // just checking access
                    // Heuristic: main browser process has a window handle or no --type= in its args
                    // We detect subprocesses by checking if they have --type= via WMI would be heavy;
                    // instead we kill all msedge processes and let Windows restart them cleanly.
                    return true;
                }
                catch { return false; }
            })
            .ToList();

        foreach (var p in mainEdgeProcs)
        {
            try { p.Kill(entireProcessTree: false); } catch { }
        }

        // Brief pause to let process handles release the profile lock
        System.Threading.Thread.Sleep(2000);

        // Launch with real profile + remote debugging
        new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--remote-debugging-port=9223 --user-data-dir=\"{profileDir}\"",
                UseShellExecute = true
            }
        }.Start();

        // Wait up to 15s for CDP endpoint to come up
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(1000);
            if (IsCdpPortUp(cdpUrl))
                return $"✅ Edge is ready with your real profile and remote debugging on port 9223. " +
                       "Your tabs should be restored. You can now say \"ready\" and I'll fill the form.";
        }

        return $"⚠️ Edge launched but the debug port at {cdpUrl} didn't respond within 15 seconds. " +
               "Edge may still be loading — try saying \"ready\" in a moment.";
    }

    private static bool IsCdpPortUp(string cdpBaseUrl)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return http.GetAsync(cdpBaseUrl + "/json/version").Result.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<IBrowser> GetFormBrowserAsync(CancellationToken cancellationToken)
    {
        // Validate cached connection is still alive
        if (_formBrowser is not null)
        {
            try { _ = _formBrowser.Contexts; return _formBrowser; }
            catch { _formBrowser = null; }
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_formBrowser is not null)
            {
                try { _ = _formBrowser.Contexts; return _formBrowser; }
                catch { _formBrowser = null; }
            }

            _playwright ??= await Playwright.CreateAsync();

            var cdpUrl = string.IsNullOrWhiteSpace(_formBrowserCdpUrl)
                ? "http://127.0.0.1:9223"
                : _formBrowserCdpUrl;

            // Only try to attach — never auto-launch (that's LaunchFormBrowser's job).
            // Auto-launching inside a fill request causes timeouts and spawns duplicate windows.
            if (!await IsCdpPortListeningAsync(cdpUrl, cancellationToken))
            {
                var profileDir = GetRealEdgeProfileDir();
                throw new InvalidOperationException(
                    $"No browser with remote debugging found at {cdpUrl}. " +
                    "Ask me to 'launch the form browser' first, or run this in PowerShell:\n" +
                    $"Start-Process 'msedge' '--remote-debugging-port=9223 --user-data-dir=\"{profileDir}\"'\n" +
                    "Once Edge opens, log into any required sites, then say \"ready\" and I'll fill the form.");
            }

            _formBrowser = await _playwright.Chromium.ConnectOverCDPAsync(cdpUrl,
                new BrowserTypeConnectOverCDPOptions { Timeout = 10000 });
            _formBrowserIsCdp = true;
            return _formBrowser;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task<bool> IsCdpPortListeningAsync(string cdpBaseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync(cdpBaseUrl + "/json/version", cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IPage> EnsureUpworkSessionPageAsync(CancellationToken cancellationToken)
    {
        var browser = await GetUpworkBrowserAsync(cancellationToken);

        if (_upworkSessionPage is not null && !_upworkSessionPage.IsClosed)
        {
            return _upworkSessionPage;
        }

        if (_upworkUsingCdp)
        {
            _upworkSessionContext ??= browser.Contexts.FirstOrDefault();
            if (_upworkSessionContext is null)
            {
                _upworkSessionContext = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
                });
            }

            var pages = _upworkSessionContext.Pages;
            _upworkSessionPage = pages.FirstOrDefault(static p =>
                    p.Url.Contains("upwork.com/ab/messages", StringComparison.OrdinalIgnoreCase) ||
                    p.Url.Contains("/rooms/", StringComparison.OrdinalIgnoreCase))
                ?? pages.LastOrDefault(static p =>
                    !string.IsNullOrWhiteSpace(p.Url) &&
                    !p.Url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase) &&
                    !p.Url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase))
                ?? await _upworkSessionContext.NewPageAsync();
        }
        else
        {
            _upworkSessionContext ??= await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
            });

            _upworkSessionPage = await _upworkSessionContext.NewPageAsync();
        }

        _upworkSessionPage.SetDefaultTimeout(_timeoutMs);
        return _upworkSessionPage;
    }

    private async Task<IBrowser> GetUpworkBrowserAsync(CancellationToken cancellationToken)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            _playwright ??= await Playwright.CreateAsync();

            if (!_upworkUsingCdp && ShouldAttemptCdpConnect())
            {
                var cdpUrl = string.IsNullOrWhiteSpace(_upworkChromeCdpUrl)
                    ? DefaultChromeCdpUrl
                    : _upworkChromeCdpUrl;
                _upworkLastCdpAttemptUtc = DateTimeOffset.UtcNow;

                // Try to connect, and if it fails, launch Chrome and retry
                var cdpConnectTimeoutMs = Math.Min(_timeoutMs, 3000);
                LogUpwork($"Attempting CDP attach to {cdpUrl} (timeout {cdpConnectTimeoutMs}ms)");
                try
                {
                    var cdpBrowser = await _playwright.Chromium.ConnectOverCDPAsync(cdpUrl, new BrowserTypeConnectOverCDPOptions
                    {
                        Timeout = cdpConnectTimeoutMs
                    });
                    if (_upworkBrowser is not null && !_upworkUsingCdp)
                    {
                        await SafeCloseBrowserAsync(_upworkBrowser);
                    }
                    _upworkBrowser = cdpBrowser;
                    _upworkUsingCdp = true;
                    _upworkCdpLastError = null;
                    _upworkSessionPage = null;
                    _upworkSessionContext = null;
                    LogUpwork("CDP attach succeeded. Upwork will use shared Chrome session.");
                    return _upworkBrowser;
                }
                catch (Exception ex)
                {
                    LogUpwork($"CDP attach failed. Attempting to launch Chrome in debug mode. Error: {ex.Message}");
                    // Try to launch Chrome
                    try
                    {
                        var chromePath = GetChromeExecutablePath();
                        var chromeArgs = "--remote-debugging-port=9222 --user-data-dir=chrome-upwork-profile";
                        LogUpwork($"Launching Chrome: {chromePath} {chromeArgs}");
                        var proc = new System.Diagnostics.Process
                        {
                            StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = chromePath,
                                Arguments = chromeArgs,
                                UseShellExecute = true
                            }
                        };
                        proc.Start();
                        // Wait for DevTools endpoint to be available
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        bool available = false;
                        while (sw.Elapsed < TimeSpan.FromSeconds(10))
                        {
                            await Task.Delay(1000, cancellationToken);
                            try
                            {
                                using var client = new System.Net.Http.HttpClient();
                                var resp = await client.GetAsync(cdpUrl, cancellationToken);
                                if (resp.IsSuccessStatusCode)
                                {
                                    available = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (!available)
                        {
                            throw new InvalidOperationException($"Chrome DevTools endpoint did not become available at {cdpUrl} after launch.");
                        }
                        LogUpwork("Chrome launched and DevTools endpoint is available. Retrying CDP attach...");
                        // Retry CDP attach
                        var cdpBrowser = await _playwright.Chromium.ConnectOverCDPAsync(cdpUrl, new BrowserTypeConnectOverCDPOptions
                        {
                            Timeout = cdpConnectTimeoutMs
                        });
                        if (_upworkBrowser is not null && !_upworkUsingCdp)
                        {
                            await SafeCloseBrowserAsync(_upworkBrowser);
                        }
                        _upworkBrowser = cdpBrowser;
                        _upworkUsingCdp = true;
                        _upworkCdpLastError = null;
                        _upworkSessionPage = null;
                        _upworkSessionContext = null;
                        LogUpwork("CDP attach succeeded after launching Chrome.");
                        return _upworkBrowser;
                    }
                    catch (Exception launchEx)
                    {
                        _upworkCdpLastError = ex.ToString() + "\nChrome launch error: " + launchEx.ToString();
                        _upworkUsingCdp = false;
                        LogUpwork($"CDP attach and Chrome launch failed. Error: {launchEx}");
                        throw new InvalidOperationException($"Failed to attach to Chrome via CDP at {cdpUrl}. Error: {ex.Message}\nChrome launch error: {launchEx.Message}", launchEx);
                    }
                }
            }

            if (_upworkBrowser is not null)
            {
                return _upworkBrowser;
            }

            _upworkBrowser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false
            });
            _upworkUsingCdp = false;
            LogUpwork("Started fallback automation browser for Upwork session.");
            return _upworkBrowser;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private bool ShouldAttemptCdpConnect()
    {
        if (_upworkUsingCdp)
        {
            return false;
        }

        if (_upworkLastCdpAttemptUtc is null)
        {
            return true;
        }

        return DateTimeOffset.UtcNow - _upworkLastCdpAttemptUtc.Value >= UpworkCdpRetryCooldown;
    }

    private static async Task SafeCloseBrowserAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync();
        }
        catch
        {
            // Best effort cleanup when switching browser strategy.
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

    private static async Task<string?> TryResolveYouTubeChannelIdAsync(HttpClient httpClient, Uri channelUri, CancellationToken cancellationToken)
    {
        if (!channelUri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = channelUri.AbsolutePath ?? string.Empty;
        if (path.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase))
        {
            return path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
        }

        var html = await httpClient.GetStringAsync(channelUri, cancellationToken);
        var channelIdMatch = Regex.Match(html, "\"channelId\":\"(?<id>[^\"]+)\"", RegexOptions.IgnoreCase);
        if (channelIdMatch.Success)
        {
            return channelIdMatch.Groups["id"].Value;
        }

        return null;
    }

    private static string BuildPodcastSearchQuery(string query)
    {
        return $"\"{query.Trim()}\" podcast latest episode -remix -song -lyrics -playlist -karaoke";
    }

    private static YouTubeSearchCandidate? SelectBestYouTubeCandidate(List<YouTubeSearchCandidate>? candidates, string query, bool podcastMode)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return null;
        }

        var usableCandidates = candidates
            .Where(static candidate => candidate is not null && !string.IsNullOrWhiteSpace(candidate.Href))
            .Select(static candidate => new YouTubeSearchCandidate(
                candidate.Href ?? string.Empty,
                candidate.Title ?? string.Empty,
                candidate.Metadata ?? string.Empty,
                candidate.AriaLabel ?? string.Empty))
            .ToList();

        if (usableCandidates.Count == 0)
        {
            return null;
        }

        if (!podcastMode)
        {
            return usableCandidates[0];
        }

        var bestCandidate = usableCandidates
            .Select(candidate => new { Candidate = candidate, Score = ScorePodcastCandidate(candidate, query) })
            .OrderByDescending(result => result.Score)
            .First();

        return bestCandidate.Score >= MinimumPodcastCandidateScore
            ? bestCandidate.Candidate
            : null;
    }

    private static int ScorePodcastCandidate(YouTubeSearchCandidate candidate, string query)
    {
        var title = candidate.Title ?? string.Empty;
        var metadata = candidate.Metadata ?? string.Empty;
        var ariaLabel = candidate.AriaLabel ?? string.Empty;
        var haystack = $"{title} {metadata} {ariaLabel}";
        var normalizedHaystack = NormalizeForSearch(haystack);
        var normalizedQuery = NormalizeForSearch(query);
        var score = 0;

        if (!string.IsNullOrWhiteSpace(normalizedQuery) && normalizedHaystack.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 40;
        }

        foreach (var term in ExtractQueryTerms(query))
        {
            if (normalizedHaystack.Contains(term, StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        foreach (var keyword in PodcastHintKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 18;
            }
        }

        foreach (var keyword in NonPodcastKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score -= 35;
            }
        }

        if (ariaLabel.Contains("hour", StringComparison.OrdinalIgnoreCase))
        {
            score += 12;
        }

        if (ariaLabel.Contains("minute", StringComparison.OrdinalIgnoreCase))
        {
            score += 6;
        }

        return score;
    }

    private static IEnumerable<string> ExtractQueryTerms(string query)
    {
        return NormalizeForSearch(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3 && !QueryStopWords.Contains(term))
            .Distinct(StringComparer.Ordinal);
    }

    private static string NormalizeForSearch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();

        return string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool ShouldUseYouTubeMusicForPodcastCandidate(YouTubeSearchCandidate candidate)
    {
        var title = candidate.Title ?? string.Empty;
        var metadata = candidate.Metadata ?? string.Empty;
        var ariaLabel = candidate.AriaLabel ?? string.Empty;
        var haystack = $"{title} {metadata} {ariaLabel}";

        return PodcastHintKeywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)) &&
               !NonPodcastKeywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryConvertToYouTubeMusicUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Host, "www.youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!uri.AbsolutePath.StartsWith("/watch", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"https://music.youtube.com{uri.PathAndQuery}";
    }

    private static bool IsYouTubeMusicUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Host, "music.youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private static WebSearchCandidate? SelectBestSpotifyAlbumCandidate(List<WebSearchCandidate>? candidates, string artistName)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        var usableCandidates = candidates
            .Where(static candidate => candidate is not null &&
                !string.IsNullOrWhiteSpace(candidate.Href) &&
                candidate.Href.Contains("open.spotify.com/album/", StringComparison.OrdinalIgnoreCase))
            .Select(static candidate => new WebSearchCandidate(
                candidate.Href ?? string.Empty,
                candidate.Title ?? string.Empty,
                candidate.Snippet ?? string.Empty))
            .ToList();

        if (usableCandidates.Count == 0)
        {
            return null;
        }

        var bestCandidate = usableCandidates
            .Select(candidate => new { Candidate = candidate, Score = ScoreSpotifyAlbumCandidate(candidate, artistName) })
            .OrderByDescending(result => result.Score)
            .First();

        return bestCandidate.Score >= MinimumSpotifyAlbumCandidateScore
            ? bestCandidate.Candidate
            : null;
    }

    private static string BuildSpotifyFocusQuery(string? activity)
    {
        if (string.IsNullOrWhiteSpace(activity))
        {
            return "instrumental focus music deep work no lyrics";
        }

        var normalized = activity.Trim().ToLowerInvariant();

        if (normalized.Contains("code", StringComparison.Ordinal) ||
            normalized.Contains("coding", StringComparison.Ordinal) ||
            normalized.Contains("program", StringComparison.Ordinal) ||
            normalized.Contains("develop", StringComparison.Ordinal))
        {
            return "coding music instrumental focus deep work no lyrics";
        }

        if (normalized.Contains("study", StringComparison.Ordinal) ||
            normalized.Contains("read", StringComparison.Ordinal) ||
            normalized.Contains("learn", StringComparison.Ordinal))
        {
            return "study music instrumental concentration no lyrics";
        }

        if (normalized.Contains("meditat", StringComparison.Ordinal) ||
            normalized.Contains("contemplat", StringComparison.Ordinal) ||
            normalized.Contains("think", StringComparison.Ordinal))
        {
            return "ambient contemplation music instrumental calm focus no lyrics";
        }

        return $"{activity.Trim()} instrumental focus music no lyrics";
    }

    private static int ScoreSpotifyAlbumCandidate(WebSearchCandidate candidate, string artistName)
    {
        var title = candidate.Title ?? string.Empty;
        var snippet = candidate.Snippet ?? string.Empty;
        var haystack = $"{title} {snippet}";
        var normalizedHaystack = NormalizeForSearch(haystack);
        var normalizedArtistName = NormalizeForSearch(artistName);
        var score = 0;

        if (candidate.Href.Contains("open.spotify.com/album/", StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }

        if (!string.IsNullOrWhiteSpace(normalizedArtistName) && normalizedHaystack.Contains(normalizedArtistName, StringComparison.Ordinal))
        {
            score += 25;
        }

        foreach (var term in ExtractQueryTerms(artistName))
        {
            if (normalizedHaystack.Contains(term, StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        if (haystack.Contains("album", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        var candidateYear = ExtractLatestYear(haystack);
        if (candidateYear >= 1950)
        {
            score += Math.Max(0, candidateYear - 2000);
        }

        return score;
    }

    private static int ExtractLatestYear(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var matches = Regex.Matches(value, @"\b(19|20)\d{2}\b");
        var latestYear = 0;

        foreach (Match match in matches)
        {
            if (int.TryParse(match.Value, out var year) && year > latestYear)
            {
                latestYear = year;
            }
        }

        return latestYear;
    }

    private sealed record YouTubeSearchCandidate(string Href, string Title, string Metadata, string AriaLabel);

    private sealed record WebSearchCandidate(string Href, string Title, string Snippet);

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

    private string BuildCdpHint()
    {
        var cdpUrl = string.IsNullOrWhiteSpace(_upworkChromeCdpUrl)
            ? DefaultChromeCdpUrl
            : _upworkChromeCdpUrl;

        var errorSuffix = string.IsNullOrWhiteSpace(_upworkCdpLastError)
            ? string.Empty
            : $" Last CDP connect error: {_upworkCdpLastError}";

        return $"To reuse your logged-in Chrome tab/session, start Chrome with remote debugging and set UPWORK_CHROME_CDP_URL={cdpUrl}.{errorSuffix}";
    }

    private static void LogUpwork(string message)
    {
        var logLine = $"[upwork.browser] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Console.WriteLine(logLine);
        try
        {
            System.IO.File.AppendAllText(UpworkLogFile, logLine + System.Environment.NewLine);
        }
        catch
        {
            // Ignore file log errors (e.g. permissions, locked file)
        }
    }

    /// <summary>
    /// Conversational fill: interpret a natural language instruction and fill a matching form field on the given page.
    /// Examples: "put John Smith in the full name field", "set email to test@example.com".
    /// The method will try to discover visible form controls (name/id/label/placeholder) and perform a fuzzy match
    /// between the user's instruction and available fields. If a value can't be inferred, a clarification message is returned.
    /// </summary>
    public async Task<string> ConversationalFillAsync(string url, string instruction, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "No URL provided.";
        if (string.IsNullOrWhiteSpace(instruction))
            return "No instruction provided.";

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        try
        {
            var browser = await GetFormBrowserAsync(cancellationToken);
            var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();

            // Find the tab already showing the target URL — never create new tabs or navigate existing ones.
            var page = FindPageForUrl(context, url);
            if (page is null)
                return $"No open tab is currently showing {url}. Please open that page in your Edge browser and try again.";

            page.SetDefaultTimeout(_timeoutMs);

                // Collect simple form element metadata from the page DOM
                var elementsJson = await page.EvaluateAsync<string>("() => { const elements = Array.from(document.querySelectorAll('input, textarea, select')); return JSON.stringify(elements.map(el => { const name = el.getAttribute('name') || ''; const id = el.id || ''; let labelText = ''; if (el.labels && el.labels.length) labelText = Array.from(el.labels).map(l=>l.textContent||'').join(' ').trim(); if(!labelText){ const parentLabel = el.closest('label'); if(parentLabel) labelText = parentLabel.textContent.trim(); } const placeholder = el.getAttribute('placeholder') || ''; const type = el.type || el.tagName.toLowerCase(); return { name, id, label: labelText, placeholder, type }; })); }");

                var doc = JsonDocument.Parse(elementsJson);
                var candidates = new List<(string name, string id, string label, string placeholder, string type)>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var name = el.GetProperty("name").GetString() ?? string.Empty;
                    var id = el.GetProperty("id").GetString() ?? string.Empty;
                    var label = el.GetProperty("label").GetString() ?? string.Empty;
                    var placeholder = el.GetProperty("placeholder").GetString() ?? string.Empty;
                    var type = el.GetProperty("type").GetString() ?? string.Empty;
                    candidates.Add((name, id, label, placeholder, type));
                }

                if (candidates.Count == 0)
                    return "No form controls detected on the page. Try focusing the right page tab and ensure the form is visible.";

                // Score candidates by token overlap with instruction
                var instrNorm = NormalizeForSearch(instruction);
                var instrTokens = instrNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                int bestScore = 0; int bestIndex = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    var hay = NormalizeForSearch(string.Join(' ', new[] { c.label, c.name, c.id, c.placeholder }));
                    var score = instrTokens.Count(t => hay.Contains(t, StringComparison.Ordinal));
                    if (score > bestScore)
                    {
                        bestScore = score; bestIndex = i;
                    }
                }

                if (bestScore == 0 || bestIndex < 0)
                {
                    return "I couldn't identify which field you meant. Please specify the field label or say something like: 'put John Smith in the full name field'.";
                }

                var field = candidates[bestIndex];

                // Try to extract the intended value from the instruction
                string? value = null;
                var m = Regex.Match(instruction, "(?:put|enter|type|fill|set)\\s+['\"]?(?<value>[^'\"']+?)['\"]?\\s+(?:in|into|to)\\s+", RegexOptions.IgnoreCase);
                if (m.Success)
                    value = m.Groups["value"].Value.Trim();

                if (string.IsNullOrEmpty(value))
                {
                    // 'set X to Y' style
                    m = Regex.Match(instruction, "(?:set|change)\\s+.+?\\s+to\\s+['\"]?(?<value>[^'\"]+)['\"]?", RegexOptions.IgnoreCase);
                    if (m.Success)
                        value = m.Groups["value"].Value.Trim();
                }

                if (string.IsNullOrEmpty(value))
                {
                    // fallback: quoted string
                    m = Regex.Match(instruction, "['\"](?<value>[^'\"]+)['\"]");
                    if (m.Success)
                        value = m.Groups["value"].Value.Trim();
                }

                if (string.IsNullOrEmpty(value))
                {
                    return "I couldn't determine the value to enter. Please say something like: 'put John Smith in the full name field' or quote the value.";
                }

                // Build selector and attempt to fill
                string selector = null;
                if (!string.IsNullOrEmpty(field.name)) selector = $"[name='{field.name}']";
                else if (!string.IsNullOrEmpty(field.id)) selector = $"#{field.id}";

                if (!string.IsNullOrEmpty(selector))
                {
                    var locator = page.Locator(selector).First;
                    try
                    {
                        await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
                        var tagName = await locator.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
                        if (tagName == "select")
                            await locator.SelectOptionAsync(value);
                        else
                            await locator.FillAsync(value);

                        var summary = $"Filled field '{(string.IsNullOrEmpty(field.label) ? (string.IsNullOrEmpty(field.name) ? field.id : field.name) : field.label)}' with '{value}'. The tab is still open — please review and submit manually.";
                        return summary;
                    }
                    catch (Exception ex)
                    {
                        return $"Failed to fill the field using selector {selector}: {ex.Message}";
                    }
                }
                else
                {
                    // As a last resort, attempt to locate the input by matching label text and set value via DOM
                    var labelText = field.label ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(labelText))
                        return "Unable to build a selector for the matched field and no label text was found.";

                    var script = @"(labelText, value) => {
                        const labels = Array.from(document.querySelectorAll('label'));
                        const lbl = labels.find(l => (l.textContent || '').trim() === (labelText || '').trim());
                        if (!lbl) return 'label-not-found';
                        const forAttr = lbl.getAttribute('for');
                        const input = forAttr ? document.getElementById(forAttr) : lbl.querySelector('input, textarea, select');
                        if (!input) return 'input-not-found';
                        input.focus();
                        if (input.tagName === 'SELECT') { input.value = value; input.dispatchEvent(new Event('change', { bubbles: true })); }
                        else { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); }
                        return 'ok';
                    }";

                    var res = await page.EvaluateAsync<string>(script, new object[] { labelText, value });
                    if (res == "ok")
                    {
                        return $"Filled field '{labelText}' with '{value}'. The tab is still open — please review and submit manually.";
                    }

                    return $"Could not fill the field by label method: {res}";
                }
        }
        catch (Exception ex)
        {
            return $"Conversational fill failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Batch conversational fill: process multiple natural language fill instructions in a single
    /// browser interaction. Discovers form controls once, then applies all instructions.
    /// Instructions should be separated by semicolons or newlines, e.g.:
    ///   "put John Smith in the full name field; put john@example.com in the email field; set country to UK"
    /// Returns a summary of each fill result.
    /// NEVER submits — the tab stays open for the user to review and submit manually.
    /// </summary>
    public async Task<string> BatchConversationalFillAsync(string url, string instructions, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "No URL provided.";
        if (string.IsNullOrWhiteSpace(instructions))
            return "No instructions provided.";

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        // Split on semicolons or newlines to get individual field instructions
        var parts = instructions
            .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (parts.Count == 0)
            return "No instructions found after parsing. Separate instructions with semicolons, e.g. 'put John in first name; put Smith in last name'.";

        try
        {
            var browser = await GetFormBrowserAsync(cancellationToken);
            var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();

            var page = FindPageForUrl(context, url);
            if (page is null)
                return $"No open tab is currently showing {url}. Please open that page in your Edge browser and try again.";

            page.SetDefaultTimeout(_timeoutMs);

            // Discover form elements once for all instructions
            var elementsJson = await page.EvaluateAsync<string>("() => { const elements = Array.from(document.querySelectorAll('input, textarea, select')); return JSON.stringify(elements.map(el => { const name = el.getAttribute('name') || ''; const id = el.id || ''; let labelText = ''; if (el.labels && el.labels.length) labelText = Array.from(el.labels).map(l=>l.textContent||'').join(' ').trim(); if(!labelText){ const parentLabel = el.closest('label'); if(parentLabel) labelText = parentLabel.textContent.trim(); } const placeholder = el.getAttribute('placeholder') || ''; const type = el.type || el.tagName.toLowerCase(); return { name, id, label: labelText, placeholder, type }; })); }");

            var doc = JsonDocument.Parse(elementsJson);
            var candidates = new List<(string name, string id, string label, string placeholder, string type)>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.GetProperty("name").GetString() ?? string.Empty;
                var id = el.GetProperty("id").GetString() ?? string.Empty;
                var label = el.GetProperty("label").GetString() ?? string.Empty;
                var placeholder = el.GetProperty("placeholder").GetString() ?? string.Empty;
                var type = el.GetProperty("type").GetString() ?? string.Empty;
                candidates.Add((name, id, label, placeholder, type));
            }

            if (candidates.Count == 0)
                return "No form controls detected on the page. Try focusing the right page tab and ensure the form is visible.";

            var results = new List<string>();

            foreach (var instruction in parts)
            {
                // Score candidates by token overlap with this instruction
                var instrNorm = NormalizeForSearch(instruction);
                var instrTokens = instrNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                int bestScore = 0;
                int bestIndex = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    var hay = NormalizeForSearch(string.Join(' ', new[] { c.label, c.name, c.id, c.placeholder }));
                    var score = instrTokens.Count(t => hay.Contains(t, StringComparison.Ordinal));
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                if (bestScore == 0 || bestIndex < 0)
                {
                    results.Add($"⚠️ '{instruction}' — could not identify which field you meant.");
                    continue;
                }

                var field = candidates[bestIndex];

                // Extract value from instruction
                string? value = null;
                var m = Regex.Match(instruction, "(?:put|enter|type|fill|set)\\s+['\"]?(?<value>[^'\"']+?)['\"]?\\s+(?:in|into|to)\\s+", RegexOptions.IgnoreCase);
                if (m.Success) value = m.Groups["value"].Value.Trim();

                if (string.IsNullOrEmpty(value))
                {
                    m = Regex.Match(instruction, "(?:set|change)\\s+.+?\\s+to\\s+['\"]?(?<value>[^'\"]+)['\"]?", RegexOptions.IgnoreCase);
                    if (m.Success) value = m.Groups["value"].Value.Trim();
                }

                if (string.IsNullOrEmpty(value))
                {
                    m = Regex.Match(instruction, "['\"](?<value>[^'\"]+)['\"]");
                    if (m.Success) value = m.Groups["value"].Value.Trim();
                }

                if (string.IsNullOrEmpty(value))
                {
                    results.Add($"⚠️ '{instruction}' — could not determine the value to enter.");
                    continue;
                }

                string? selector = null;
                if (!string.IsNullOrEmpty(field.name)) selector = $"[name='{field.name}']";
                else if (!string.IsNullOrEmpty(field.id)) selector = $"#{field.id}";

                var displayLabel = !string.IsNullOrEmpty(field.label) ? field.label
                    : !string.IsNullOrEmpty(field.name) ? field.name
                    : field.id;

                if (!string.IsNullOrEmpty(selector))
                {
                    try
                    {
                        var locator = page.Locator(selector).First;
                        await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
                        var tagName = await locator.EvaluateAsync<string>("el => el.tagName.toLowerCase()");
                        if (tagName == "select")
                            await locator.SelectOptionAsync(value);
                        else
                            await locator.FillAsync(value);
                        results.Add($"✅ '{displayLabel}' → '{value}'");
                    }
                    catch (Exception ex)
                    {
                        results.Add($"❌ '{displayLabel}' — fill failed: {ex.Message}");
                    }
                }
                else if (!string.IsNullOrEmpty(field.label))
                {
                    // Label-based fallback via DOM
                    var script = @"(labelText, value) => {
                        const labels = Array.from(document.querySelectorAll('label'));
                        const lbl = labels.find(l => (l.textContent || '').trim() === (labelText || '').trim());
                        if (!lbl) return 'label-not-found';
                        const forAttr = lbl.getAttribute('for');
                        const input = forAttr ? document.getElementById(forAttr) : lbl.querySelector('input, textarea, select');
                        if (!input) return 'input-not-found';
                        input.focus();
                        if (input.tagName === 'SELECT') { input.value = value; input.dispatchEvent(new Event('change', { bubbles: true })); }
                        else { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); }
                        return 'ok';
                    }";
                    var res = await page.EvaluateAsync<string>(script, new object[] { field.label, value });
                    if (res == "ok")
                        results.Add($"✅ '{displayLabel}' → '{value}'");
                    else
                        results.Add($"❌ '{displayLabel}' — label fill failed: {res}");
                }
                else
                {
                    results.Add($"⚠️ '{instruction}' — no selector or label available for matched field.");
                }
            }

            var summary = string.Join("\n", results);
            return $"Batch fill complete ({results.Count(r => r.StartsWith("✅"))} of {parts.Count} succeeded):\n{summary}\n\nThe tab is still open — please review and submit manually.";
        }
        catch (Exception ex)
        {
            return $"Batch fill failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Walk the accessibility tree and collect form control nodes into <paramref name="fields"/>.
    /// Password-like fields are excluded and a warning is added instead.
    /// </summary>
    private static void FlattenFormControls(
        System.Text.Json.JsonElement node,
        List<System.Text.Json.JsonElement> fields,
        List<string> warnings)
    {
        var role = node.TryGetProperty("role", out var rp) ? rp.GetString() ?? "" : "";
        var name = node.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";

        var formControlRoles = new[] { "textbox", "combobox", "listbox", "checkbox", "radio", "searchbox", "spinbutton", "slider" };

        if (formControlRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("passcode", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Password field detected ('{name}') — excluded from AI context. Please type it manually.");
            }
            else
            {
                var obj = new System.Text.Json.Nodes.JsonObject { ["role"] = role, ["label"] = name };

                if (node.TryGetProperty("required", out var req) && req.ValueKind == System.Text.Json.JsonValueKind.True)
                    obj["required"] = true;

                if (node.TryGetProperty("value", out var val) && val.ValueKind != System.Text.Json.JsonValueKind.Null)
                    obj["currentValue"] = val.GetString();

                // Collect options for combobox / listbox
                if ((role == "combobox" || role == "listbox") &&
                    node.TryGetProperty("children", out var kids))
                {
                    var opts = new System.Text.Json.Nodes.JsonArray();
                    foreach (var kid in kids.EnumerateArray())
                    {
                        if (kid.TryGetProperty("role", out var kr) && kr.GetString() == "option" &&
                            kid.TryGetProperty("name", out var kn))
                            opts.Add(kn.GetString());
                    }
                    if (opts.Count > 0)
                        obj["options"] = opts;
                }

                var raw = obj.ToJsonString();
                fields.Add(System.Text.Json.JsonDocument.Parse(raw).RootElement.Clone());
            }
        }

        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
                FlattenFormControls(child, fields, warnings);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_upworkSessionPage is not null && !_upworkSessionPage.IsClosed && !_upworkUsingCdp)
        {
            await _upworkSessionPage.CloseAsync();
        }
        _upworkSessionPage = null;

        if (_upworkSessionContext is not null && !_upworkUsingCdp)
        {
            await _upworkSessionContext.CloseAsync();
        }
        _upworkSessionContext = null;

        if (_upworkBrowser is not null && !_upworkUsingCdp)
        {
            await _upworkBrowser.CloseAsync();
        }
        _upworkBrowser = null;

        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        // For CDP-connected form browser: disconnect only (don't close the user's actual browser process)
        if (_formBrowser is not null && !_formBrowserIsCdp)
        {
            await SafeCloseBrowserAsync(_formBrowser);
        }
        _formBrowser = null;

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
