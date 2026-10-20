using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

internal sealed class WhatsAppAssistantService : IAsyncDisposable
{
    private const int MaxContentLength = 10000;
    private const string WhatsAppWebUrl = "https://web.whatsapp.com";
    private const string DefaultChromeCdpUrl = "http://127.0.0.1:9222";
    private readonly bool _headless;
    private readonly int _timeoutMs;
    private readonly string? _chromeCdpUrl;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private bool _initialized;

    private WhatsAppAssistantService(bool headless, int timeoutMs, string? chromeCdpUrl)
    {
        _headless = headless;
        _timeoutMs = timeoutMs;
        _chromeCdpUrl = chromeCdpUrl ?? DefaultChromeCdpUrl;
    }

    public static WhatsAppAssistantService FromEnvironment()
    {
        var headless = EnvironmentSettings.ReadOptionalBool("WHATSAPP_HEADLESS") ?? true;
        var timeoutMs = EnvironmentSettings.ReadOptionalInt("WHATSAPP_TIMEOUT_MS") ?? 30000;
        var chromeCdpUrl = EnvironmentSettings.ReadOptionalString("WHATSAPP_CHROME_CDP_URL");

        return new WhatsAppAssistantService(headless, timeoutMs, chromeCdpUrl);
    }

    private async Task InitializeAsync()
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized)
                return;

            _playwright = await Playwright.CreateAsync();

            try
            {
                // Try to connect to existing CDP instance first
                _browser = await _playwright.Chromium.ConnectOverCDPAsync(_chromeCdpUrl!);
            }
            catch
            {
                // Fall back to launching local browser
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = _headless,
                    Args = new[] { "--disable-blink-features=AutomationControlled" }
                });
            }

            _page = await _browser.NewPageAsync();
            _page.SetDefaultTimeout(_timeoutMs);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Extract messages from a WhatsApp group within the last N days.
    /// </summary>
    public async Task<List<WhatsAppMessage>> ExtractGroupMessagesAsync(string groupName, int daysBack = 30)
    {
        await InitializeAsync();

        if (_page == null)
            throw new InvalidOperationException("WhatsApp page not initialized.");

        try
        {
            // Navigate to WhatsApp Web
            await _page.GotoAsync(WhatsAppWebUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            // Wait for chat list to load
            await _page.WaitForSelectorAsync("[role='listitem']", new PageWaitForSelectorOptions { Timeout = _timeoutMs });

            // Find the group chat by name
            var groupChat = await FindGroupChatAsync(groupName);
            if (groupChat == null)
                throw new InvalidOperationException($"Group '{groupName}' not found in WhatsApp.");

            // Click on the group to open it
            await groupChat.ClickAsync();
            await _page.WaitForSelectorAsync("[data-testid='msg-container']", new PageWaitForSelectorOptions { Timeout = _timeoutMs });

            // Extract messages
            var messages = await ExtractMessagesFromCurrentChatAsync(daysBack);

            return messages;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to extract WhatsApp messages from '{groupName}': {ex.Message}", ex);
        }
    }

    private async Task<ILocator?> FindGroupChatAsync(string groupName)
    {
        if (_page == null)
            return null;

        // Get all chat list items
        var chatItems = _page.Locator("[role='listitem']");
        var count = await chatItems.CountAsync();

        for (int i = 0; i < count; i++)
        {
            var item = chatItems.Nth(i);
            var text = await item.TextContentAsync();

            if (text?.Contains(groupName, StringComparison.OrdinalIgnoreCase) ?? false)
                return item;
        }

        return null;
    }

    private async Task<List<WhatsAppMessage>> ExtractMessagesFromCurrentChatAsync(int daysBack)
    {
        if (_page == null)
            return new List<WhatsAppMessage>();

        var messages = new List<WhatsAppMessage>();
        var cutoffDate = DateTime.UtcNow.AddDays(-daysBack);
        var messageSelector = "[data-testid='msg']";
        var lastHeight = 0;

        try
        {
            // Scroll up to load older messages until we reach the cutoff date or top
            while (true)
            {
                var messageElements = await _page.QuerySelectorAllAsync(messageSelector);

                foreach (var element in messageElements)
                {
                    var msg = await ExtractMessageAsync(element, cutoffDate);
                    if (msg != null)
                        messages.Add(msg);
                }

                // Check if we've reached messages older than our cutoff
                var hasOlderMessages = messages.Any(m => m.Timestamp < cutoffDate);
                if (hasOlderMessages)
                    break;

                // Scroll up to load more messages
                var currentHeight = (int)await _page.EvaluateAsync<double>("window.scrollY");
                if (currentHeight == lastHeight)
                    break; // No more messages to load

                lastHeight = currentHeight;
                await _page.EvaluateAsync("window.scrollBy(0, -1000)");
                await _page.WaitForTimeoutAsync(500); // Throttle scrolling
            }

            // Filter messages to only include those within the date range
            messages = messages
                .Where(m => m.Timestamp >= cutoffDate && m.Timestamp <= DateTime.UtcNow)
                .OrderBy(m => m.Timestamp)
                .ToList();
        }
        catch (Exception ex)
        {
            // Log error but continue with partial results
            Debug.WriteLine($"Error extracting messages: {ex.Message}");
        }

        return messages;
    }

    private async Task<WhatsAppMessage?> ExtractMessageAsync(IElementHandle element, DateTime cutoffDate)
    {
        try
        {
            var text = await element.TextContentAsync();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Parse timestamp from message (format varies by locale)
            var timestamp = ParseMessageTimestamp(text);
            if (timestamp == null || timestamp < cutoffDate)
                return null;

            // Extract sender name (if group message)
            var senderElement = await element.QuerySelectorAsync("[data-testid='msg-sender']");
            var sender = await senderElement?.TextContentAsync();

            // Extract message body (remove timestamp and metadata)
            var body = CleanMessageText(text, sender);

            return new WhatsAppMessage(
                sender ?? "Unknown",
                body,
                timestamp.Value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error extracting single message: {ex.Message}");
            return null;
        }
    }

    private DateTime? ParseMessageTimestamp(string text)
    {
        // Common WhatsApp timestamp patterns: "10:30", "10:30 AM", "Yesterday 10:30", "Monday 10:30"
        var timestampPatterns = new[]
        {
            @"(\d{1,2}):(\d{2})\s*(?:AM|PM)?",
            @"(Today|Yesterday|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\s+(\d{1,2}):(\d{2})",
            @"(\d{1,2})/(\d{1,2})/(\d{2,4})\s+(\d{1,2}):(\d{2})"
        };

        foreach (var pattern in timestampPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                // For simplicity, try to parse and return current date with time
                // A more robust implementation would fully parse the date
                if (int.TryParse(match.Groups[1].Value, out int hour) &&
                    int.TryParse(match.Groups[2].Value, out int minute))
                {
                    return DateTime.UtcNow.Date.AddHours(hour).AddMinutes(minute);
                }
            }
        }

        return null;
    }

    private string CleanMessageText(string text, string? sender)
    {
        // Remove timestamps and sender names from the raw text
        var cleaned = text;

        if (!string.IsNullOrEmpty(sender))
            cleaned = cleaned.Replace(sender, "").Trim();

        // Remove time patterns
        cleaned = Regex.Replace(cleaned, @"(\d{1,2}):(\d{2})\s*(?:AM|PM)?", "");

        // Remove extra whitespace
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        return cleaned;
    }

    public async ValueTask DisposeAsync()
    {
        if (_page != null)
            await _page.CloseAsync();

        if (_browser != null)
            await _browser.CloseAsync();

        _playwright?.Dispose();
    }
}

internal record WhatsAppMessage(string Sender, string Body, DateTime Timestamp);
