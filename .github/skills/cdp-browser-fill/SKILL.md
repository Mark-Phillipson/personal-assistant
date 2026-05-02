---
name: cdp-browser-fill
description: 'Fill web forms via Chrome DevTools Protocol (CDP) over WebSocket from PowerShell on Windows. Use when Playwright is not available and the task is to populate form fields in a running Edge or Chrome browser tab.'
argument-hint: 'Describe which tab and what values to fill, or provide the URL and field values.'
name: playwright-browser-fill
description: 'Fill web forms using Playwright (.NET) by attaching to a running Chromium/Edge instance over CDP or launching with a user profile. Prefer this when Playwright is available.'
argument-hint: 'Provide URL and selector/value pairs; optional remote debugging port (default 9222).'
user-invocable: true
---

# Playwright Browser Fill (Windows / .NET)

This skill replaces low-level CDP messaging with a Playwright (.NET) workflow that can attach to an existing Chromium/Edge instance over the Chrome DevTools Protocol (CDP) or launch a browser that uses a user profile. Playwright gives more robust selectors, automatic waiting, and simpler high-level operations.

**When to use:**
- Use Playwright `.ConnectOverCDPAsync(...)` to attach to a running browser that was started with `--remote-debugging-port` (this preserves logged-in sessions if the browser was launched with the same profile).
- If you cannot attach, launch a Playwright browser with a `userDataDir` that points to a profile copy (avoid launching the same profile while an interactive browser is running).

## Pre-flight — start the target browser with CDP

1. Close all Edge/Chrome windows.
2. Launch Edge/Chrome with remote debugging enabled. Example Edge command (PowerShell):

```powershell
& 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' --remote-debugging-port=9222
```

If you want to reuse your normal profile and stay logged in, ensure you start the browser using the same profile (and that no other browser instance is running that keeps the profile locked). The repository's existing "Edge Remote Debugging" shortcut typically does this — use that.

Confirm the remote debugging endpoint is available:

```powershell
$info = Invoke-RestMethod -Uri http://127.0.0.1:9222/json/version -UseBasicParsing
$info.webSocketDebuggerUrl
```

The returned `webSocketDebuggerUrl` (example: `ws://127.0.0.1:9222/devtools/browser/<id>`) is the endpoint Playwright can use to attach.

## Install Playwright (.NET)

From your project folder:

```powershell
dotnet add package Microsoft.Playwright
dotnet tool install --global Microsoft.Playwright.CLI   # optional
playwright install
```

You can also trigger binaries installation at runtime with:

```csharp
await Microsoft.Playwright.Playwright.InstallAsync();
```

## Example — Attach to a running browser and fill fields (C#)

Create a small `.NET` console app and use the endpoint found above. This example connects to the running browser, finds or creates a page, and fills inputs using Playwright's selector API.

```csharp
using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;

class PlaywrightAttachExample
{
    public static async Task Main(string[] args)
    {
        // Optionally pass the CDP webSocket URL as the first arg.
        var endpoint = args.Length > 0 ? args[0] : await GetWebSocketEndpointAsync();

        using var playwright = await Playwright.CreateAsync();

        // Attach to the running Chromium/Edge instance over CDP
        var browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint);

        // Try to reuse an existing page, otherwise create one
        var page = browser.Contexts.SelectMany(c => c.Pages).FirstOrDefault();
        if (page == null)
        {
            var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
            page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        }

        // Example: navigate (optional) and fill a selector
        if (args.Length > 1) await page.GotoAsync(args[1]);

        // Example fill operations (change selectors to match target page)
        await page.FillAsync("input[name=\"email\"]", "you@example.com");
        await page.FillAsync("input[name=\"password\"]", "hunter2");
        await page.ClickAsync("button[type=submit]");

        Console.WriteLine("Form fill completed (attached). Leave browser open to keep session).");
    }

    static async Task<string> GetWebSocketEndpointAsync()
    {
        using var client = new HttpClient();
        var json = await client.GetStringAsync("http://127.0.0.1:9222/json/version");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
    }
}
```

Notes:
- `ConnectOverCDPAsync` accepts either a `ws://...` `webSocketDebuggerUrl` or the HTTP CDP endpoint; use the `webSocketDebuggerUrl` returned by `/json/version` for reliability.
- Do not call `browser.CloseAsync()` if you want to preserve the running user's session; disposing Playwright objects is fine but closing the underlying browser will end the session.

## Quick PowerShell helper (find endpoint)

```powershell
#$ws = (Invoke-RestMethod -Uri http://127.0.0.1:9222/json/version -UseBasicParsing).webSocketDebuggerUrl
#dotnet run --project .\PlaywrightAttachExample\PlaywrightAttachExample.csproj $ws https://example.com/login
```

## VS Code integrated browser

The VS Code "Integrated Browser" or some preview extensions do not always expose a CDP endpoint. Playwright needs a real Chromium/Edge process launched with `--remote-debugging-port`. If you want to automate a VS Code preview, check whether that extension exposes a CDP port or consider launching a normal Edge/Chrome instance with the desired profile.

## Troubleshooting & tips
- If `ConnectOverCDPAsync` fails, confirm the `webSocketDebuggerUrl` is reachable from the machine and that the browser was started with the debugging port.
- Reusing an interactive profile is possible but delicate: prefer to start the browser for automation with the same `user-data-dir` only when no other process holds the profile lock.
- If you control launching the browser from code, `playwright.Chromium.LaunchPersistentContextAsync(userDataDir, options)` can start a browser using a persistent profile and give you full control.

---

If you want, I can (1) add the console app sample as a runnable project in `scripts/` and (2) try a local run to verify the attach flow on your machine — which would require you to confirm Edge is started with remote debugging. Which would you like next?
            break
        }
    } catch {}
}
if (-not $wsUrl) { Write-Error 'Target tab not found. Check browser has remote debugging and the correct tab is open.'; return }
```

---

## Step 2 — Build the CDP WebSocket Client

**CRITICAL: Never use PowerShell generic type syntax like `[System.ArraySegment[byte]]` — it does not compile with `Add-Type`.** Always write C# to a temp `.cs` file and use `Add-Type -Path`.

Write the client once per session — check if the type already exists first:

```powershell
if (-not ([System.Management.Automation.PSTypeName]'CdpClient').Type) {
    $cs = @'
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

public class CdpClient {
    private ClientWebSocket ws = new ClientWebSocket();

    public void Connect(string uri) {
        ws.ConnectAsync(new Uri(uri), CancellationToken.None).Wait();
    }

    public void Send(string msg) {
        byte[] buf = Encoding.UTF8.GetBytes(msg);
        ws.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
    }

    // Loop until the response with matching "id" arrives — ignore background events
    public string ReceiveUntilId(int id, int timeoutMs) {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        byte[] buf = new byte[131072];
        string idToken = "\"id\":" + id.ToString();
        while (DateTime.UtcNow < deadline) {
            MemoryStream ms = new MemoryStream();
            WebSocketReceiveResult result;
            do {
                int remaining = Math.Max(200, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                CancellationTokenSource cts = new CancellationTokenSource(remaining);
                Task<WebSocketReceiveResult> t = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                t.Wait();
                result = t.Result;
                if (result.Count > 0) ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);
            string m = Encoding.UTF8.GetString(ms.ToArray());
            if (m.IndexOf(idToken) >= 0) return m;
        }
        return "timeout";
    }

    public void Close() {
        try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).Wait(); } catch {}
    }
}
'@
    $csPath = "$env:TEMP\CdpClient.cs"
    Set-Content -Encoding UTF8 -Path $csPath -Value $cs
    Add-Type -Path $csPath
}
```

---

## Step 3 — Inspect the DOM First

**Always run a DOM inspection before attempting to fill fields.** Forms often have no `<label>` elements — field names may be `addr1` not `address`, `zip` not `postal`, etc.

```powershell
$cdp = New-Object CdpClient
$cdp.Connect($wsUrl)

$inspectJs = @'
var inputs = Array.from(document.querySelectorAll('input,textarea,select')).map(function(i){
    return { tag: i.tagName, id: i.id, name: i.name, type: i.type,
             placeholder: i.placeholder, 'aria-label': i.getAttribute('aria-label') };
});
var labels = Array.from(document.querySelectorAll('label')).map(function(l){
    return { text: l.textContent.trim(), for: l.getAttribute('for') };
});
JSON.stringify({ inputs: inputs, labels: labels });
'@

$jsJson = $inspectJs | ConvertTo-Json -Depth 1
$cdp.Send('{"id":1,"method":"Runtime.evaluate","params":{"expression":' + $jsJson + ',"returnByValue":true}}')
$resp = $cdp.ReceiveUntilId(1, 8000) | ConvertFrom-Json
$domInfo = $resp.result.result.value | ConvertFrom-Json
Write-Output "=== Inputs ==="
$domInfo.inputs | Format-Table -AutoSize
Write-Output "=== Labels ==="
$domInfo.labels | Format-Table -AutoSize
```

Review the output and note the exact `name` or `id` attributes before proceeding.

---

## Step 4 — Fill the Fields

Use the actual `name` attributes discovered in Step 3. Target by `name` first, then fall back to `id` or `placeholder`.

```powershell
# Build fill JS using exact field names from Step 3 inspection
$fillJs = @'
function s(n, v) {
    var el = document.querySelector('input[name="' + n + '"],textarea[name="' + n + '"]');
    if (!el) el = document.getElementById(n);
    if (!el) el = document.querySelector('[placeholder="' + n + '"]');
    if (el) {
        el.focus();
        el.value = v;
        el.dispatchEvent(new Event('input',  { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    }
    return false;
}
var r = {};
// ===== REPLACE with actual field names from Step 3 =====
r.name    = s('name',    'VALUE');
r.addr1   = s('addr1',   'VALUE');
r.city    = s('city',    'VALUE');
r.zip     = s('zip',     'VALUE');
r.email   = s('email',   'VALUE');
// =======================================================
JSON.stringify(r);
'@

$jsJson = $fillJs | ConvertTo-Json -Depth 1
$cdp.Send('{"id":2,"method":"Runtime.evaluate","params":{"expression":' + $jsJson + ',"returnByValue":true}}')
$resp = $cdp.ReceiveUntilId(2, 8000)
Write-Output "Fill results: $resp"
$cdp.Close()
```

Any field returning `false` means the selector didn't match — go back to Step 3, re-check the `name`/`id`, and fix the selector.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Port 9222 not responding | Normal Edge running instead of debug version | **Tell user:** close all Edge windows, then launch from the **desktop shortcut** or the **Voice Admin launches table** entry for remote debugging Edge |
| `Tab not found` | Debug Edge open but wrong tab, or wrong URL fragment | Ask user to navigate to the correct page; fix URL fragment |
| All fields return `false` | Wrong `name` attributes | Run Step 3 inspection and use exact values returned |
| Response is a background event (no `"id":N`) | Old CDP client exited too early | `ReceiveUntilId` loops until matching ID — don't use a simple single `Receive` |
| `Add-Type` compile error about char literals | Used `'` quotes inside C# `Add-Type` heredoc | Write C# to a `.cs` file with `Set-Content`, then `Add-Type -Path` |
| `timeout` response | Tab navigated away, or very slow page | Increase `timeoutMs`; confirm tab URL hasn't changed |
| Fields filled but values disappear | Framework (React/Vue) resets state | Dispatch `input` + `change` + optionally `blur`; or use `Object.getOwnPropertyDescriptor` to set the React internal value |
| Multiple Edge instances competing | Both instances use port 9222/9223 | **Tell user:** close all Edge, relaunch via desktop shortcut or Voice Admin launches table |

---

## React / Vue Framework Forms

If the site uses React or Vue, plain `el.value = v` may not persist. Use the React internal setter:

```javascript
function setReact(el, val) {
    var nativeInputValueSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    nativeInputValueSetter.call(el, val);
    el.dispatchEvent(new Event('input', { bubbles: true }));
}
```

---

## Quick Reference Checklist

- [ ] **User told** to close all Edge windows and launch the remote debugging version (desktop shortcut or Voice Admin launches table)
- [ ] Port 9222 confirmed live (`/json/version` returns browser info)
- [ ] Correct tab found (check by URL fragment or title)
- [ ] `CdpClient` type loaded via `Add-Type -Path` (never inline C# with generic types in PowerShell heredoc)
- [ ] DOM inspected — actual `name`/`id` attributes confirmed before filling
- [ ] Fill JS uses exact attribute values
- [ ] `input` and `change` events dispatched after setting value
- [ ] All fields returned `true` in fill results

## Session Notes — Integrated Browser Test (2026-05-02)

Summary of what we learned during a live test of the Upwork messages composer using VS Code's Integrated Browser and Playwright:

- **Integrated browser limitations:** The VS Code integrated browser does not expose a CDP endpoint — prefer attaching Playwright to a real Edge/Chrome instance launched with `--remote-debugging-port=9222`. The integrated browser can still navigate and render, but automation that requires CDP (or a persistent profile attach) will fail.
- **Login redirect handling:** Attempting to open a message-room URL redirected to Upwork's login page if not signed in. Ensure a logged-in session exists in the target browser/profile before attempting to fill message drafts.
- **Composer selector strategy used:** We searched for these selectors (in order) and used the first match:
    - `textarea`
    - `input[type="text"]`
    - `[contenteditable="true"]`
    - `div[role="textbox"]`
    - `[aria-label*="message"]`
    - `[placeholder*="message"]`
    - `[data-test="message-composer"]`
    If a single selector didn't match, fall back to scanning all `[contenteditable="true"]` elements and choose the visible one.
- **Filling approach:** Clear the element, focus it, then:
    1. Set `value` or `innerText` to empty.
    2. Dispatch `input` and `change` events.
    3. Use `page.keyboard.type(...)` to insert the draft (helps frameworks pick up changes reliably).
    4. Dispatch `input`/`change` again and optionally `blur`.
- **Framework compatibility:** For React/Vue-managed inputs, prefer the native setter pattern shown in the skill (use Object.getOwnPropertyDescriptor to set and dispatch input events).
- **Network and GraphQL errors:** During the session Upwork returned some GraphQL 404/403 and third-party resource failures. Automation must tolerate partial failures (suggestions may be unavailable).
- **Safety / non-send guarantee:** The script deliberately avoided invoking the send action. When automating message compose, never call click/send unless explicitly commanded.
- **Example test draft used:** `Test draft: Hello from Copilot — this is a test message. DO NOT SEND.`

Recommended next steps:

- Add the Playwright script from `scripts/PlaywrightAttachExample` to this repo (if not present) and include a `--dry-run` mode that fills a composer but never presses send.
- Document the recommended `--remote-debugging-port=9222` workflow and provide a shortcut or script to launch Edge with a safe persistent profile for automation.

