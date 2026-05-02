using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace PlaywrightAttachExample
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            string endpoint = null;
            string targetUrl = null;
            string messageText = "Test draft: Hello from Copilot — this is a test message. DO NOT SEND.";
            bool send = false;
            bool installBrowsers = false;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--endpoint": case "-e":
                        if (i + 1 < args.Length) endpoint = args[++i];
                        break;
                    case "--url": case "-u":
                        if (i + 1 < args.Length) targetUrl = args[++i];
                        break;
                    case "--text": case "-t":
                        if (i + 1 < args.Length) messageText = args[++i];
                        break;
                    case "--send":
                        send = true;
                        break;
                    case "--install":
                        installBrowsers = true;
                        break;
                    default:
                        Console.WriteLine($"Unknown arg: {a}");
                        break;
                }
            }

            try
            {
                if (string.IsNullOrEmpty(endpoint)) endpoint = await GetWebSocketEndpointAsync();

                using var playwright = await Playwright.CreateAsync();

                if (installBrowsers)
                {
                    Console.WriteLine("Installing Playwright browsers (may take a while)...");
                    await Microsoft.Playwright.Playwright.InstallAsync();
                }

                IBrowser browser = null;
                if (!string.IsNullOrEmpty(endpoint))
                {
                    Console.WriteLine($"Connecting to CDP endpoint: {endpoint}");
                    browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint);
                }
                else
                {
                    Console.WriteLine("No CDP endpoint found. Launching persistent context for demo (non-headless).");
                    var userDataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playwright-profile");
                    var context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, new BrowserTypeLaunchPersistentContextOptions { Headless = false });
                    browser = context.Browser;
                }

                var contextToUse = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
                var page = contextToUse.Pages.FirstOrDefault() ?? await contextToUse.NewPageAsync();

                if (!string.IsNullOrEmpty(targetUrl))
                {
                    Console.WriteLine($"Navigating to {targetUrl} ...");
                    await page.GotoAsync(targetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                }

                string[] candidates = new[]
                {
                    "textarea",
                    "input[type=\"text\"]",
                    "[contenteditable=\"true\"]",
                    "div[role=\"textbox\"]",
                    "[aria-label*=\"message\"]",
                    "[placeholder*=\"message\"]",
                    "[data-test=\"message-composer\"]"
                };

                string chosen = null;
                foreach (var sel in candidates)
                {
                    try
                    {
                        var q = await page.QuerySelectorAsync(sel);
                        if (q != null && await page.IsVisibleAsync(sel))
                        {
                            chosen = sel;
                            break;
                        }
                    }
                    catch { }
                }

                if (chosen == null)
                {
                    var handles = await page.QuerySelectorAllAsync("[contenteditable=\"true\"]");
                    foreach (var h in handles)
                    {
                        try
                        {
                            if (await h.IsVisibleAsync())
                            {
                                chosen = "[contenteditable=\"true\"]";
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (chosen == null)
                {
                    Console.WriteLine("Composer field not found with heuristics.");
                    return 2;
                }

                Console.WriteLine($"Composer selector chosen: {chosen}");

                var element = await page.QuerySelectorAsync(chosen);
                if (element == null)
                {
                    Console.WriteLine("Element disappeared before interaction.");
                    return 3;
                }

                var tagName = await element.EvaluateAsync<string>("e => e.tagName");
                if (tagName == "TEXTAREA" || tagName == "INPUT")
                {
                    Console.WriteLine("Filling input/textarea...");
                    await element.FillAsync(messageText);
                }
                else
                {
                    Console.WriteLine("Setting contenteditable / non-input element via evaluate + events...");
                    await page.EvaluateAsync(@"(args) => {
                        const sel = args.selector;
                        const text = args.text;
                        const el = document.querySelector(sel);
                        if (!el) return false;
                        if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') {
                            el.value = text;
                            el.dispatchEvent(new Event('input',{bubbles:true}));
                            el.dispatchEvent(new Event('change',{bubbles:true}));
                            return true;
                        }
                        const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
                        if (nativeSetter && el.nodeName === 'INPUT') {
                            nativeSetter.call(el, text);
                        } else {
                            el.innerText = text;
                        }
                        el.dispatchEvent(new Event('input',{bubbles:true}));
                        el.dispatchEvent(new Event('change',{bubbles:true}));
                        return true;
                    }", new { selector = chosen, text = messageText });
                }

                Console.WriteLine("Dispatching final events and blurring.");
                await page.EvaluateAsync("(sel) => { const el = document.querySelector(sel); if (!el) return; el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true})); el.blur && el.blur(); }", chosen);

                if (send)
                {
                    Console.WriteLine("Send requested. Searching for send button...");
                    string[] sendSels = new[] { "button[type=submit]", "button[aria-label*=\"send\"]", "button[data-test*=\"send\"]" };
                    string sendSelFound = null;
                    foreach (var s in sendSels)
                    {
                        try
                        {
                            var q = await page.QuerySelectorAsync(s);
                            if (q != null && await page.IsVisibleAsync(s))
                            {
                                sendSelFound = s;
                                break;
                            }
                        }
                        catch { }
                    }
                    if (sendSelFound != null)
                    {
                        Console.WriteLine($"Clicking send button {sendSelFound}");
                        await page.ClickAsync(sendSelFound);
                    }
                    else
                    {
                        Console.WriteLine("Send button not found; aborting send.");
                    }
                }
                else
                {
                    Console.WriteLine("Dry-run mode: not sending the message.");
                }

                Console.WriteLine("Done. Keep the process running if you want to inspect the browser.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex}");
                return 99;
            }
        }

        static async Task<string> GetWebSocketEndpointAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(2);
                var json = await client.GetStringAsync("http://127.0.0.1:9222/json/version");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var p))
                    return p.GetString();
            }
            catch { }
            return null;
        }
    }
}
using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace PlaywrightAttachExample
{
    class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var endpoint = args.Length > 0 ? args[0] : await GetWebSocketEndpointAsync();

                using var playwright = await Playwright.CreateAsync();

                var browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint);

                var page = browser.Contexts.SelectMany(c => c.Pages).FirstOrDefault();
                if (page == null)
                {
                    var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
                    page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
                }

                if (args.Length > 1)
                    await page.GotoAsync(args[1]);

                // Fill common fields with test data (no submit).
                var filled = await page.EvaluateAsync<int>(@"() => {
                    function setValue(selector, value) {
                        const el = document.querySelector(selector);
                        if (!el) return 0;
                        const tag = el.tagName.toLowerCase();
                        if (tag === 'select') {
                            if (el.options.length) el.value = el.options[0].value || el.options[0].text;
                            el.dispatchEvent(new Event('change', { bubbles: true }));
                            return 1;
                        }
                        if (tag === 'input' || tag === 'textarea') {
                            el.focus();
                            el.value = value;
                            el.dispatchEvent(new Event('input', { bubbles: true }));
                            el.dispatchEvent(new Event('change', { bubbles: true }));
                            el.blur();
                            return 1;
                        }
                        return 0;
                    }

                    let count = 0;
                    // Name field (try placeholder, name, id)
                    count += setValue('input[placeholder="Enter Name"]', 'Test Launcher');
                    if (count === 0) count += setValue('input[name*="name"]', 'Test Launcher');
                    if (count === 0) count += setValue('input[id*="name"]', 'Test Launcher');

                    // Command Line / Arguments / Working Directory / Sort Order
                    count += setValue('input[placeholder*="Command Line"]', 'test-command');
                    count += setValue('textarea[placeholder*="Command Line"]', 'test-command');
                    count += setValue('input[placeholder*="Arguments"]', 'arg1 arg2');
                    count += setValue('textarea[placeholder*="Arguments"]', 'arg1 arg2');
                    count += setValue('input[placeholder*="Working Directory"]', 'C:\\temp');
                    count += setValue('input[placeholder*="Sort Order"]', '1');

                    // Primary category select (best-effort)
                    const sel = document.querySelector('select[name*="category"], select[id*="category"], select[aria-label*="Primary Category"]');
                    if (sel && sel.options && sel.options.length) {
                        sel.value = sel.options[Math.min(1, sel.options.length-1)].value || sel.options[0].value;
                        sel.dispatchEvent(new Event('change', { bubbles: true }));
                        count++;
                    }

                    // Additional categories: check first checkbox if present
                    const cb = document.querySelector('input[type=checkbox][name*="category"], .additional-categories input[type=checkbox]');
                    if (cb) { cb.checked = true; cb.dispatchEvent(new Event('change', { bubbles: true })); count++; }

                    return count;
                }");

                Console.WriteLine($"Auto-filled {filled} control(s) (no submit).");

                Console.WriteLine("Form fill completed (attached). Leave browser open to keep session).");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 2;
            }
        }

        static async Task<string> GetWebSocketEndpointAsync()
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync("http://127.0.0.1:9222/json/version");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
        }
    }
}
