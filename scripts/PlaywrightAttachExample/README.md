# PlaywrightAttachExample

This small console app demonstrates attaching Playwright to a running Chromium/Edge instance over CDP (or launching a persistent context) and filling a message composer without sending.

Usage

1. (Optional) Start Edge with remote debugging enabled. Example PowerShell:

```powershell
& 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' --remote-debugging-port=9222 --user-data-dir="C:\temp\edge-playwright-profile"
```

2. Restore / build and run the project. From the repo root:

```powershell
dotnet restore
dotnet run --project .\scripts\PlaywrightAttachExample -- --url "https://www.upwork.com/ab/messages/rooms/room_..." --text "Test draft: Hello from Copilot — DO NOT SEND."
```

3. To attach to an existing CDP endpoint pass `--endpoint` (ws://... or the HTTP /json/version value). To request Playwright to install browsers, pass `--install`.

Examples

Attach to local Edge launched with `--remote-debugging-port=9222` (auto-discovered):

```powershell
dotnet run --project .\scripts\PlaywrightAttachExample -- --url "https://www.upwork.com/ab/messages/rooms/room_..." --text "Test draft: DO NOT SEND."
```

Attach and *actually* send (use with caution):

```powershell
dotnet run --project .\scripts\PlaywrightAttachExample -- --url "https://..." --text "Hello" --send
```

Notes

- Default behavior is dry-run (will not send unless `--send` is provided).
- The integrated VS Code browser does not expose a CDP endpoint; to attach Playwright use a real Edge/Chrome started with `--remote-debugging-port=9222`.
- This example intentionally focuses on safe filling and event dispatching; it may need selector tweaks for specific sites (Upwork uses dynamic selectors and GraphQL requests).
# PlaywrightAttachExample

Tiny .NET console app that demonstrates attaching Playwright to a running Chromium/Edge instance over CDP and performing simple form fills.

Prerequisites
- .NET SDK matching `TargetFramework` in the project (net10.0 in this sample). Adjust `TargetFramework` in the `.csproj` if needed.
- A Chromium/Edge process started with `--remote-debugging-port=9222` (or change the port used by the example).

Quick setup

```powershell
# from repository root
dotnet restore
dotnet add ./scripts/PlaywrightAttachExample/PlaywrightAttachExample.csproj package Microsoft.Playwright
dotnet tool install --global Microsoft.Playwright.CLI
playwright install
```

Run the example

```powershell
# Get the CDP websocket endpoint
$ws = (Invoke-RestMethod -Uri http://127.0.0.1:9222/json/version -UseBasicParsing).webSocketDebuggerUrl

# Run the example: pass the websocket URL and an optional URL to navigate to
dotnet run --project .\scripts\PlaywrightAttachExample\PlaywrightAttachExample.csproj -- $ws https://example.com/login
```

Notes
- Change the selectors in `Program.cs` to match the target page.
- If you prefer to launch a browser from code using a persistent profile, see `browserType.LaunchPersistentContextAsync(userDataDir, options)`.
