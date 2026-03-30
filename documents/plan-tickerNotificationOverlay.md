# Plan: Voice-Friendly Notification Ticker Overlay

**Date:** March 30, 2026  
**Status:** Planning

---

## Problem

Windows toast notifications require individual mouse clicks to dismiss — impossible hands-free for a voice-controlled user. A persistent, looping, voice-dismissable ticker is needed instead.

---

## Solution Overview

Build a `TickerOverlayForm` in the **NaturalCommands** project (which already has WinForms/WPF), expose it via a new CLI mode, and call it from the **personal-assistant** bot via a new `TickerNotificationService`.

Optionally (Phase 5), add a `WindowsNotificationListenerService` inside NaturalCommands that taps the Windows notification pipeline via `UserNotificationListener` and routes every notification from every app straight into the ticker — so nothing ever pops up as a blocking toast again.

---

## Architecture

### Phases 1–4 (assistant-only notifications)
```
personal-assistant (bot process)
  └── TickerNotificationService
        └── writes messages to temp file
              └── Process.Start → NaturalCommands.exe ticker-file <path>
                    └── TickerOverlayForm (WinForms, TopMost, full-width bar)
```

### Phase 5 (intercept ALL Windows notifications)
```
NaturalCommands.exe (always-running tray process)
  └── WindowsNotificationListenerService
        └── UserNotificationListener.Current  ← WinRT API (SDK already in project)
              ├── NotificationChanged event → captures every new system toast
              ├── RemoveNotification(id)     → dismisses it from Action Center
              ├── maps app name → TickerCategory (configurable)
              └── feeds TickerOverlayForm queue

WindowsFocusAssistService (already exists in personal-assistant)
  └── SetFocusAssistModeAsync("on")  ← suppresses native toast popups
        ↑ called once at startup to prevent double-display
```

---

## Phase 1 — `TickerOverlayForm` in NaturalCommands

### New files
- `TickerOverlayForm.cs`
- `TickerOverlayForm.Designer.cs`

### Message Categories

Each message carries a **category** that controls the icon and accent colour shown on the ticker bar:

| Category | Icon | Accent Colour | Use case |
|---|---|---|---|
| `info` | ℹ️ | Steel blue `#2196F3` | General assistant updates, status messages |
| `warning` | ⚠️ | Amber `#FFC107` | Non-critical issues, reminders, upcoming deadlines |
| `critical` | 🔴 | Red `#F44336` | Errors, calendar conflicts, urgent alerts |
| `success` | ✅ | Green `#4CAF50` | Completed tasks, confirmations |

Default category when none is specified: `info`.

### Behaviour
- Full-width bar pinned to the **bottom** of the primary screen (configurable to top)
- Reads a newline-delimited message list from a file path passed via CLI args (see message format below)
- Cycles through messages one at a time on a configurable timer (default: 5 seconds), looping back to start
- **Critical messages pause the auto-cycle timer** and display a pulsing red border until manually dismissed
- Displays message counter and category icon: `ℹ️ [2/5]  Calendar reminder: Stand-up in 10 minutes`
- Left accent stripe changes colour per category
- Buttons (large, keyboard/voice-accessible via Talon letter labels):
  - **Dismiss All** — closes the overlay and clears all queued messages
  - **Next** — manually advance to the next message
  - **Prev** — manually go back to the previous message
- `TopMost = true`, `FormBorderStyle = None`, semi-transparent dark background
- Closes on **Escape** key or **Dismiss All** click
- Non-blocking: process stays open until dismissed or all messages have cycled N times

### UI Layout (sketch)
```
┌═╦──────────────────────────────────────────────────────────────────────────┐
│ ║  ℹ️ [2/5]  Calendar reminder: Stand-up in 10 minutes    [Prev] [Next] [X] │
└═╩──────────────────────────────────────────────────────────────────────────┘
  ↑ coloured accent stripe (blue=info, amber=warning, red=critical, green=success)
```

Critical example:
```
┌═╦──────────────────────────────────────────────────────────────────────────┐
│ ║  🔴 [1/5]  ERROR: Gmail API token expired — action required  [Prev][Next][X]│
└═╩──────────────────────────────────────────────────────────────────────────┘
  ↑ red pulsing border, auto-cycle paused
```

### Implementation notes
- Extend the existing overlay pattern from `UIElementOverlayForm` and `AutoClickOverlayForm`
- Reuse `DisplayMessage.SharedBackColor` / `SharedForeColor` for consistent styling
- Use a `System.Windows.Forms.Timer` for cycling; pause on mouse-hover and on `critical` messages
- Support `--position top|bottom` optional flag
- Draw the left accent stripe in `OnPaint` using the current message's category colour
- Use a second timer for the pulsing border animation on `critical` messages (toggle border colour every 500 ms)

---

## Phase 2 — CLI Mode in `Program.cs` (NaturalCommands)

Add two new CLI modes to the existing `<mode> <args>` contract:

```
NaturalCommands.exe ticker "info:Message one|warning:Meeting in 5 minutes|critical:API error"
NaturalCommands.exe ticker-file "C:\Users\...\ticker_messages.txt"
```

### Message file format

Each line in the file (or each pipe-delimited segment for inline mode) is:
```
<category>:<message text>
```
Examples:
```
info:Gmail sync completed — 3 new emails
warning:Calendar: Stand-up starts in 10 minutes
critical:Google OAuth token has expired — re-authentication required
success:Podcast download finished: .NET Rocks #1923
```
If no `category:` prefix is present the line is treated as `info`.

- `ticker` — pipe-separated inline messages with optional category prefix
- `ticker-file` — path to newline-delimited text file (for longer content from the assistant)
- Shows the overlay in a background thread; `Main()` returns immediately (fire-and-forget)
- Optional: block until dismissed by passing `--wait` flag so the assistant can await cleanup

### `Program.cs` changes
```csharp
case "ticker":
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    var messages = args[1].Split('|', StringSplitOptions.RemoveEmptyEntries);
    Application.Run(new TickerOverlayForm(messages));
    return;

case "ticker-file":
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    var lines = File.ReadAllLines(args[1]);
    Application.Run(new TickerOverlayForm(lines));
    return;
```

---

## Phase 3 — `TickerNotificationService` in personal-assistant

### New file: `TickerNotificationService.cs`

Responsibilities:
- Maintain an in-memory `Queue<TickerMessage>` of pending notification messages
- `Enqueue(string message, TickerCategory category = Info)` — add a message to the queue
- `FlushAsync()` — write all queued messages to a temp file and launch `NaturalCommands.exe ticker-file <path>`
- `EnqueueAndFlushAsync(string message, TickerCategory category)` — convenience: enqueue then immediately flush (for urgent single messages)
- **Critical messages trigger an immediate auto-flush** regardless of queue size
- Auto-flush when queue reaches a configurable threshold (default: 5 messages)
- Reads `NATURAL_COMMANDS_EXECUTABLE` from env (reuses the same path already configured)

```csharp
public enum TickerCategory { Info, Warning, Critical, Success }

public record TickerMessage(string Text, TickerCategory Category = TickerCategory.Info);

internal sealed class TickerNotificationService
{
    private readonly Queue<TickerMessage> _pending = new();
    private readonly string _executable;
    private readonly int _autoFlushThreshold;

    public void Enqueue(string message, TickerCategory category = TickerCategory.Info) { ... }
    public async Task FlushAsync() { ... }
    public async Task EnqueueAndFlushAsync(string message, TickerCategory category = TickerCategory.Info) { ... }

    // Serialises to "category:message" lines for the temp file
    private static string FormatLine(TickerMessage m) =>
        $"{m.Category.ToString().ToLowerInvariant()}:{m.Text}";
}
```

---

## Phase 4 — Wiring in personal-assistant

### `AssistantToolsFactory.cs`
Add a new `AIFunction` tool:

```
Tool name:        show_ticker_notifications
Description:      Display queued notifications in the on-screen ticker overlay.
                  Call this when the user asks to see their notifications,
                  reminders, or alerts without using Windows toast popups.
Parameters:       none (flushes whatever is queued)

Tool name:        send_ticker_notification
Description:      Immediately show a single notification in the on-screen ticker.
                  Use category='critical' for errors or urgent alerts,
                  'warning' for reminders and upcoming deadlines,
                  'success' for confirmations, 'info' for general updates.
Parameters:
  message  (string, required)  — the notification text
  category (string, optional)  — info | warning | critical | success  (default: info)
```

### `TelegramMessageHandler.cs` / `Program.cs`
- Register `TickerNotificationService` as a singleton
- Replace (or supplement) any current Windows toast calls with `tickerService.Enqueue(message)`
- Add Telegram command `/ticker` as a manual trigger to flush and display all queued items

---

---

## Phase 5 — Intercept All Windows Notifications (optional)

> Not pie in the sky — this is a real Windows API. NaturalCommands already has `Microsoft.Windows.SDK.NET` in its `.csproj`, which exposes `UserNotificationListener`.

### How it works

`Windows.UI.Notifications.Management.UserNotificationListener` is a WinRT API that lets a trusted app:
1. **Request listener access** — the user grants permission once in Windows Settings › Privacy › Notifications (or via a permission dialog)
2. **Subscribe to `NotificationChanged`** — fires for every toast from every app (Outlook, Teams, Chrome, Windows Update, etc.)
3. **Read notification content** — app display name, title, body text, timestamp
4. **Dismiss from Action Center** — `RemoveNotification(notificationId)` so it doesn't pile up

### New file: `WindowsNotificationListenerService.cs` (NaturalCommands)

```csharp
internal sealed class WindowsNotificationListenerService
{
    private readonly TickerOverlayQueue _queue;   // shared with TickerOverlayForm
    private UserNotificationListener? _listener;

    public async Task<bool> RequestAccessAsync()
    {
        var status = await UserNotificationListener.Current.RequestAccessAsync();
        return status == UserNotificationListenerAccessStatus.Allowed;
    }

    public void Start()
    {
        _listener = UserNotificationListener.Current;
        _listener.NotificationChanged += OnNotificationChanged;
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added) return;
        var notification = sender.GetNotification(args.UserNotificationId);
        var appName = notification.AppInfo.DisplayInfo.DisplayName;
        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        var title   = binding?.GetTextElements().FirstOrDefault()?.Text ?? appName;
        var body    = binding?.GetTextElements().Skip(1).FirstOrDefault()?.Text ?? "";
        var category = MapAppToCategory(appName);   // configurable rules
        _queue.Enqueue(string.IsNullOrEmpty(body) ? title : $"{title} — {body}", category);
        sender.RemoveNotification(args.UserNotificationId);   // dismiss from Action Center
    }
}
```

### Category mapping (configurable JSON file)

A `notification-category-rules.json` file maps app names or keywords to categories:
```json
[
  { "app": "Microsoft Outlook",   "category": "warning" },
  { "app": "Microsoft Teams",     "category": "info"    },
  { "app": "Windows Update",      "category": "warning" },
  { "app": "Windows Security",    "category": "critical"},
  { "app": "Chrome",              "category": "info"    },
  { "default":                    "category": "info"    }
]
```

### Access permission

The `UserNotificationListener` API works in **unpackaged (non-MSIX)** apps on Windows 10/11 — the user just needs to grant access once:

> **Windows Settings › System › Notifications › Allow apps to access notifications → NaturalCommands: On**

NaturalCommands can prompt for this automatically on first run, or the user can enable it manually. No MSIX packaging required.

### Integration with Focus Assist

- At startup, `WindowsNotificationListenerService.Start()` calls `WindowsFocusAssistService.SetFocusAssistModeAsync("on")` (via the personal-assistant service, already wired to the registry) to suppress native toast popups
- When NaturalCommands is exited, Focus Assist is restored to its previous state
- A tray icon menu item lets the user toggle interception on/off

### Opt-out / app allowlist

- A `notification-ignore-rules.json` file lists apps whose notifications should still appear natively (e.g. login prompts, UAC, screen share notifications)
- Emergency/security notifications (Windows Defender, BitLocker) are never suppressed

---

## Key Design Decisions

| Decision | Choice | Reason |
|---|---|---|
| Where UI lives | NaturalCommands | Already has WinForms/WPF; no UI deps needed in bot process |
| IPC mechanism | Temp file + process args | Simple, reliable, no named-pipe complexity |
| Dismiss model | Single "Dismiss All" button | Voice-friendly — one Talon click clears everything |
| Process lifecycle | Non-blocking overlay stays open | Assistant continues working; ticker auto-closes after N cycles or on dismiss |
| Screen position | Bottom of primary screen | Doesn't obscure work area; matches news ticker convention |
| Message format | Newline-delimited text file | Easy to write from .NET; survives long messages and special chars |
| Windows interception | `UserNotificationListener` WinRT API | Already available via `Microsoft.Windows.SDK.NET`; no MSIX needed |
| Focus Assist pairing | Enable on start, restore on exit | Prevents native toasts showing alongside the ticker |

---

## Files to Create / Modify

### NaturalCommands project (`c:\Users\MPhil\source\repos\NaturalCommands\`)

| File | Action |
|---|---|
| `TickerOverlayForm.cs` | Create — WinForms form implementing the ticker UI |
| `TickerOverlayForm.Designer.cs` | Create — designer boilerplate |
| `WindowsNotificationListenerService.cs` | Create *(Phase 5)* — intercepts all system notifications via `UserNotificationListener` |
| `notification-category-rules.json` | Create *(Phase 5)* — maps app names to ticker categories |
| `notification-ignore-rules.json` | Create *(Phase 5)* — apps whose toasts should still appear natively |
| `Program.cs` | Modify — add `ticker`, `ticker-file` CLI modes; wire listener on startup |
| `NaturalCommands.csproj` | Modify — add `<Compile Update>` entries for new form |

### personal-assistant project (`c:\Users\MPhil\source\repos\personal-assistant\`)

| File | Action |
|---|---|
| `TickerNotificationService.cs` | Create — queue + flush service |
| `AssistantToolsFactory.cs` | Modify — wire `show_ticker_notifications` AIFunction |
| `Program.cs` | Modify — register `TickerNotificationService` as singleton |

---

## Open Questions / Future Enhancements

- [ ] Support multi-monitor: should the ticker appear on the monitor with the active window?
- [ ] Persist unread messages to a file so they survive a bot restart
- [ ] Add a `/ticker-clear` Telegram command to drop queued items without displaying
- [ ] Consider an animation (slide-in/fade) for better readability
- [ ] TTS integration: read the current message aloud when it cycles (triggered by `TextToSpeechService`); vary voice tone by category
- [ ] Allow the assistant to set custom display duration per message (e.g., calendar reminders stay longer)
- [ ] Filter ticker view by category (e.g., show only `critical` messages via `/ticker critical`)
- [ ] Sound cues per category: soft chime for `info`, alert tone for `critical`
- [ ] *(Phase 5)* Tray icon menu to pause/resume notification interception without exiting NaturalCommands
- [ ] *(Phase 5)* Notification history view: `/ticker-history` Telegram command lists the last N intercepted notifications with their source app
- [ ] *(Phase 5)* Per-app snooze: suppress notifications from a specific app for N minutes via voice command
