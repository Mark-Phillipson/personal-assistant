---
name: cdp-browser-fill
description: 'Fill web forms via Chrome DevTools Protocol (CDP) over WebSocket from PowerShell on Windows. Use when Playwright is not available and the task is to populate form fields in a running Edge or Chrome browser tab.'
argument-hint: 'Describe which tab and what values to fill, or provide the URL and field values.'
user-invocable: true
---

# CDP Browser Form Fill (Windows / PowerShell)

Fill web forms in a live browser tab using the Chrome DevTools Protocol directly from PowerShell — no Playwright or Node.js required.

## Step 0 — Browser Pre-flight ⚠️ REQUIRED

**Before doing anything else**, tell the user:

> **Please close all Microsoft Edge windows now, then launch the remote debugging version of Edge.**
> You can launch it from:
> - **Your desktop** — there should be a shortcut labelled something like *"Edge Remote Debugging"*
> - **The Voice Admin launches table** — search for *"edge remote"* or *"remote debug"* and launch that entry, in the work links category.

Wait for the user to confirm Edge is open before continuing.

---

**Why this matters:** If a normal Edge instance is already running, it occupies the debugging port and the automation silently fails — all field fills return `false` and no error is shown.

Once the user confirms, verify the debugging port is live:

```powershell
# Confirm the debug port is responding
try {
    $check = Invoke-RestMethod -Uri "http://127.0.0.1:9222/json/version" -TimeoutSec 3
    Write-Output "Edge remote debugging is active: $($check.Browser)"
} catch {
    Write-Output "⚠️  Port 9222 not responding. Ask the user to relaunch the remote debugging Edge shortcut."
}
```

If the port check fails, remind the user again:

> The remote debugging port isn't open. Please fully close Edge and relaunch it using the **desktop shortcut** or the **Voice Admin launches table** entry for remote debugging Edge.

---

## Step 1 — Discover the Tab

Scan ports 9222–9232 to find the tab. Always use a loop — the port can vary.

```powershell
$wsUrl = $null
$tabTitle = $null
for ($p = 9222; $p -le 9232; $p++) {
    try {
        $list = Invoke-RestMethod -Uri "http://127.0.0.1:$p/json/list" -TimeoutSec 2 -ErrorAction Stop
        # Match by partial URL or title — adjust filter as needed
        $tab = $list | Where-Object { $_.url -like '*YOUR_URL_FRAGMENT*' } | Select-Object -First 1
        if ($tab) {
            $wsUrl = $tab.webSocketDebuggerUrl
            $tabTitle = $tab.title
            Write-Output "Found tab: $tabTitle on port $p"
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
