# Plan: Android Companion App — Minimum Viable Product

**Date:** March 30, 2026  
**Status:** Planning  
**Parent research:** [research android mobile interaction.md](research%20android%20mobile%20interaction.md)

---

## Goal

Build a standalone Android app (separate from the personal-assistant repo) that proves two things:

1. **Hands-free command entry** — the user can speak a command without manually opening Telegram and tapping the mic each time.
2. **Basic phone control** — the assistant can open apps, open URLs, and perform simple global actions (Back, Home, scroll) on the device.

Success criteria: the user can trigger a voice command from a persistent notification or Quick Settings tile, the command reaches the assistant, and the assistant can respond with an action that the phone executes — all without touching the screen more than once.

---

## Constraints

- **No root required.** Must work on stock Android 10+.
- **No ADB.** The ADB prototype already proved feasibility but requires developer mode; this MVP targets a non-developer-friendly path.
- **Separate repository.** Do not pollute the personal-assistant codebase.
- **Kotlin + Jetpack.** Standard modern Android stack.
- **Minimal scope.** Only implement what is needed to prove feasibility; polish comes later.

---

## Architecture Overview

```
┌──────────────────────────────┐
│  Android Companion App       │
│                              │
│  ┌────────────────────────┐  │         ┌──────────────────────┐
│  │ Command Entry UI       │  │         │ personal-assistant   │
│  │ (Push-to-talk tile /   │──POST───►  │ (existing .NET bot)  │
│  │  persistent notif /    │  │  JSON   │                      │
│  │  wake-word listener)   │  │         │ New /api/command     │
│  └────────────────────────┘  │         │ endpoint             │
│                              │         │                      │
│  ┌────────────────────────┐  │         │ Processes command,   │
│  │ Action Executor        │◄─POLL/WS── │ returns action JSON  │
│  │ (Accessibility Service │  │         └──────────────────────┘
│  │  + Intent launcher)    │  │
│  └────────────────────────┘  │
│                              │
│  ┌────────────────────────┐  │
│  │ Feedback Display       │  │
│  │ (Toast / overlay /     │  │
│  │  Telegram confirmation)│  │
│  └────────────────────────┘  │
└──────────────────────────────┘
```

For the MVP, the communication channel is a simple HTTP relay: the companion app POSTs command text to the assistant, and either polls for the response or receives it synchronously.

---

## Shared Command Schema (from research)

These are the commands the MVP must support. Keep names stable — they will map to Talon intents later.

| Command | Parameters | MVP priority |
|---|---|---|
| `device.open_app` | `name: string` | **Must have** |
| `device.open_url` | `url: string` | **Must have** |
| `device.navigate` | `action: back \| home \| recents \| notifications` | **Must have** |
| `device.scroll` | `direction: up \| down \| left \| right` | **Should have** |
| `device.tap_text` | `text: string` | **Nice to have** (requires Accessibility Service) |
| `device.type_text` | `text: string` | **Nice to have** (requires Accessibility Service) |
| `device.media` | `action: play \| pause \| next \| previous` | **Should have** |

---

## Phase 0 — Project Scaffolding

### Task 0.1: Create Android project
- New repo: `android-companion` (or similar).
- Android Studio project, Kotlin, min SDK 26 (Android 8.0), target SDK 34+.
- Single-module Gradle setup.
- Add dependencies: Retrofit (HTTP), OkHttp, Kotlin Coroutines, Jetpack Compose (or View-based — whichever is faster to build).

### Task 0.2: Configuration screen
- Simple settings activity/screen with fields for:
  - **Assistant URL** — the base URL of the personal-assistant HTTP endpoint (e.g. `http://192.168.1.x:5000`).
  - **Device token** — a shared secret for authenticating commands (prevents unauthorized access).
- Store in SharedPreferences (encrypted via EncryptedSharedPreferences).

### Task 0.3: Networking layer
- Retrofit interface with a single POST endpoint:
  ```kotlin
  @POST("/api/command")
  suspend fun sendCommand(@Body command: CommandRequest): CommandResponse
  ```
- `CommandRequest`: `{ "command": "open youtube", "deviceToken": "xxx" }`
- `CommandResponse`: `{ "actions": [{ "type": "device.open_app", "params": { "name": "youtube" } }], "textResponse": "Opening YouTube" }`
- Include device token header or body field for auth.

---

## Phase 1 — Command Entry (Hands-Free Input)

### Task 1.1: Quick Settings Tile
- Implement a `TileService` subclass.
- On tile tap:
  1. Launch a transparent/minimal overlay activity.
  2. Start Android `SpeechRecognizer` (system speech-to-text).
  3. On result, POST the transcribed text to the assistant.
  4. Show brief toast/overlay with assistant's text response.
  5. Execute any returned actions (Phase 2).
  6. Close overlay.
- Register tile in `AndroidManifest.xml`.

### Task 1.2: Persistent notification action
- Show a persistent foreground notification (required for Foreground Service on Android 8+).
- Notification action button: "Speak Command".
- Tapping action triggers the same speech → POST → execute flow as the tile.
- This gives the user a second trigger path that's always visible.

### Task 1.3: (Optional) Bluetooth headset button trigger
- Register a `MediaButtonReceiver` or `MediaSession` callback.
- Map media button press → start speech capture.
- This is the lowest-friction entry but hardware-dependent; defer if time-constrained.

**Deliverable:** User can tap a Quick Settings tile or notification button, speak a command, and see the transcribed text POST to the assistant. At this stage the assistant doesn't need to return actions — just an acknowledgement text response displayed as a toast.

---

## Phase 2 — Action Execution (Phone Control)

### Task 2.1: Intent-based app launcher
- Map `device.open_app(name)` to `PackageManager.getLaunchIntentForPackage()`.
- Maintain a simple lookup: resolve app name → package name by querying installed apps and fuzzy-matching on the app label.
- Fallback: open Play Store search if app not found.

### Task 2.2: URL opener
- Map `device.open_url(url)` to `Intent(ACTION_VIEW, Uri.parse(url))`.

### Task 2.3: Global navigation actions
- Map `device.navigate(action)` to `AccessibilityService.performGlobalAction()`:
  - `back` → `GLOBAL_ACTION_BACK`
  - `home` → `GLOBAL_ACTION_HOME`
  - `recents` → `GLOBAL_ACTION_RECENTS`
  - `notifications` → `GLOBAL_ACTION_NOTIFICATIONS`
- This requires the Accessibility Service (Task 2.5).

### Task 2.4: Media controls
- Map `device.media(action)` to dispatching `KeyEvent` via `AudioManager`:
  - `play`/`pause` → `KEYCODE_MEDIA_PLAY_PAUSE`
  - `next` → `KEYCODE_MEDIA_NEXT`
  - `previous` → `KEYCODE_MEDIA_PREVIOUS`

### Task 2.5: Accessibility Service (minimal)
- Implement `AccessibilityService` subclass.
- For the MVP, only use it for:
  - `performGlobalAction()` (Task 2.3)
  - `device.scroll(direction)` via `AccessibilityService.dispatchGesture()` with a swipe `GestureDescription`
- Declare in `AndroidManifest.xml` with `accessibility_service_config.xml`.
- Prompt user to enable in Settings on first launch (provide a button that opens the Accessibility settings page).
- **Do NOT implement node-based tap/type for MVP** — those are Phase 2+ in the research doc.

### Task 2.6: Action dispatcher
- Central `ActionExecutor` class that takes a `CommandResponse` and routes each action to the correct handler (2.1–2.4).
- Returns structured result per action: `{ "action": "device.open_app", "success": true }`.
- Log every action and result for debugging.

**Deliverable:** User speaks "open YouTube" → app opens. User speaks "go back" → phone navigates back. User speaks "pause music" → media pauses.

---

## Phase 3 — Assistant-Side Endpoint

This phase adds the HTTP endpoint to the existing **personal-assistant** .NET project. It's listed here for completeness but can be done independently.

### Task 3.1: Add minimal HTTP listener
- Add a lightweight Kestrel endpoint (or `HttpListener`) alongside the existing Telegram polling loop.
- Route: `POST /api/command`
- Accept JSON body: `{ "command": "...", "deviceToken": "..." }`
- Validate device token against `ANDROID_DEVICE_TOKEN` env var.
- Forward command text to the Copilot session (same as a Telegram message).
- Return JSON: `{ "actions": [...], "textResponse": "..." }`

### Task 3.2: Action extraction from assistant response
- The Copilot model returns natural language. Parse the response for action intents.
- Option A (simple): Add an AIFunction tool `execute_device_action(type, params)` that the model can call when it determines a device action is needed. The tool serializes the action into the response JSON.
- Option B (simpler): The model's text response is sent as `textResponse`, and the companion app also displays it. For MVP, the companion does its own intent parsing client-side (e.g. if response contains "Opening YouTube", the companion maps "YouTube" → launch intent). **Not recommended long-term but viable for MVP.**
- **Recommended for MVP: Option A** — it keeps the command schema clean and uses existing tool infrastructure.

### Task 3.3: Register device action tool
- In `AssistantToolsFactory.cs`, add:
  ```
  execute_device_action(actionType: string, parameters: Dictionary<string, string>)
  ```
  - Description: "Execute an action on the user's Android phone. Supported types: device.open_app, device.open_url, device.navigate, device.scroll, device.media"
  - The tool doesn't execute anything itself; it serializes the action into a response payload that the companion app will execute.
  - Return value: JSON string of the action for the HTTP response to pick up.

### Task 3.4: System prompt update
- Add guidance to `SystemPromptBuilder.cs`:
  > When the user asks to do something on their phone (open an app, navigate, play/pause media, scroll, open a URL on their device), use the `execute_device_action` tool with the appropriate action type and parameters. Do not try to use PC-based tools for phone actions.

---

## Security Requirements

- **Device token authentication** — every request from companion to assistant must include a pre-shared token. Reject requests with missing/invalid tokens.
- **Action allowlist** — the companion app only executes actions from the defined command schema. Arbitrary shell commands or intents are rejected.
- **HTTPS in production** — for LAN MVP, HTTP is acceptable; document that HTTPS is required before exposing the endpoint externally.
- **No sensitive data in logs** — do not log the device token or full request bodies in release builds.
- **Accessibility Service scope** — request only the minimum capabilities needed; do not request `FLAG_RETRIEVE_INTERACTIVE_WINDOWS` or `canRetrieveWindowContent` unless needed for Phase 2+ tap/type features.

---

## File Structure (Android Project)

```
android-companion/
├── app/
│   ├── src/main/
│   │   ├── java/com/markphillipson/companion/
│   │   │   ├── MainActivity.kt              — Settings screen + enable accessibility prompt
│   │   │   ├── CommandService.kt             — Foreground service + persistent notification
│   │   │   ├── SpeechCaptureActivity.kt      — Transparent overlay for speech recognition
│   │   │   ├── CommandTileService.kt         — Quick Settings tile
│   │   │   ├── CompanionAccessibilityService.kt — Accessibility Service for global actions + scroll
│   │   │   ├── ActionExecutor.kt             — Routes actions to correct handler
│   │   │   ├── AppLauncher.kt                — Resolves app name → package, launches
│   │   │   ├── api/
│   │   │   │   ├── AssistantApi.kt           — Retrofit interface
│   │   │   │   ├── CommandRequest.kt         — Request model
│   │   │   │   └── CommandResponse.kt        — Response model with action list
│   │   │   └── settings/
│   │   │       └── SettingsRepository.kt     — EncryptedSharedPreferences wrapper
│   │   ├── res/
│   │   │   ├── xml/
│   │   │   │   └── accessibility_service_config.xml
│   │   │   ├── drawable/
│   │   │   │   └── ic_mic.xml                — Tile/notification icon
│   │   │   └── values/
│   │   │       └── strings.xml
│   │   └── AndroidManifest.xml
│   └── build.gradle.kts
├── build.gradle.kts
├── settings.gradle.kts
└── README.md
```

---

## Task Checklist

| # | Task | Phase | Depends On | Est. Complexity |
|---|---|---|---|---|
| 0.1 | Create Android project + Gradle setup | 0 | — | Low |
| 0.2 | Settings screen (URL + device token) | 0 | 0.1 | Low |
| 0.3 | Networking layer (Retrofit + models) | 0 | 0.1 | Low |
| 1.1 | Quick Settings tile + speech capture | 1 | 0.3 | Medium |
| 1.2 | Persistent notification + speak action | 1 | 0.3, 1.1 | Low |
| 1.3 | Bluetooth headset button trigger | 1 | 1.1 | Low (optional) |
| 2.1 | Intent-based app launcher | 2 | 0.3 | Low |
| 2.2 | URL opener | 2 | 0.3 | Trivial |
| 2.3 | Global navigation (back/home/recents) | 2 | 2.5 | Low |
| 2.4 | Media controls | 2 | — | Low |
| 2.5 | Accessibility Service (minimal) | 2 | 0.1 | Medium |
| 2.6 | Action dispatcher | 2 | 2.1–2.5 | Low |
| 3.1 | HTTP endpoint in personal-assistant | 3 | — | Medium |
| 3.2 | Action extraction (AIFunction tool) | 3 | 3.1 | Low |
| 3.3 | Register device action tool | 3 | 3.2 | Low |
| 3.4 | System prompt update | 3 | 3.3 | Trivial |

---

## Testing Plan

### On-device (manual)
1. Install app, configure assistant URL and token.
2. Tap Quick Settings tile → speak "open YouTube" → verify YouTube opens.
3. Tap notification action → speak "go back" → verify phone navigates back.
4. Speak "play music" → verify media plays/pauses.
5. Speak "open google.com" → verify browser opens to URL.
6. Speak "scroll down" → verify page scrolls.
7. Verify invalid token is rejected (change token in app, confirm 401).

### Assistant-side
8. POST to `/api/command` with valid token → verify 200 + action JSON.
9. POST with invalid token → verify 401.
10. POST with unrecognized command → verify text response without actions.

### Latency measurement
11. Time from tile tap to speech result display (target: < 3 seconds).
12. Time from speech result to action execution (target: < 1 second for local actions).
13. Time from speech result to assistant response (target: < 2 seconds on LAN).

---

## What This MVP Does NOT Include

- Node-based UI inspection (tap specific UI element by text/id) — deferred to post-MVP.
- Type-into-field via Accessibility — deferred to post-MVP.
- Always-listening wake word — deferred to post-MVP.
- End-to-end encryption — use HTTPS when exposed externally.
- Offline/queued commands — requires network for MVP.
- Per-app command packs — post-MVP customization.
- Talon phrase mapping — post-MVP once command schema is validated.

---

## Recommended Build Order

1. **Phase 0** (scaffolding) — get the project compiling with a settings screen and network layer.
2. **Phase 3, Task 3.1 only** — stand up the HTTP endpoint in personal-assistant so the app has something to talk to. Can return a hardcoded response initially.
3. **Phase 1** (command entry) — get speech capture working end-to-end (app → assistant → text response displayed).
4. **Phase 2** (action execution) — wire up local actions so the assistant's response actually controls the phone.
5. **Phase 3, Tasks 3.2–3.4** — add the AIFunction tool so the assistant can return structured actions instead of just text.

This order gives the fastest path to an end-to-end demo while allowing incremental testing at each step.
