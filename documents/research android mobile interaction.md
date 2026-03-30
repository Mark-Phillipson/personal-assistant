## Research: Android Mobile Interaction for RSI Accessibility

## Context
- The personal assistant already controls parts of the PC.
- The same assistant is available from Telegram on mobile.
- Main blocker: no reliable phone control path for opening apps, browsing, and UI actions.
- Additional blocker: command entry itself is physically costly (open Telegram, open keyboard, tap microphone each time).
- Current workaround is Android Voice Access, but latency is too high and command grammar differs from Talon.

## Short Answer
Yes, we can make this work. Android does not allow full remote control by default, but a combination of:
1. an Android companion app with an Accessibility Service, and
2. command relay from the existing assistant
can provide practical hands-free control for most daily tasks.

## What Is Technically Possible (No Root)
- Open apps directly by package/activity.
- Open URLs in browser or specific apps.
- Perform global actions (Home, Back, Recents, notifications, quick settings).
- Scroll, click, and type in many apps using Accessibility node matching.
- Read visible UI text/element tree to support intent-based commands.
- Execute media controls (play/pause/next) and some notification actions.

## Hard Limits (No Root)
- Not every app exposes accessible UI nodes equally.
- Secure surfaces (lock screen, banking flows, some system dialogs) are restricted.
- Some toggles/settings are OEM- or Android-version-dependent.
- Gesture precision can vary by device and layout changes.

## Critical Missed Requirement: Zero-Touch Command Entry
Even perfect phone automation is not enough if issuing the command requires repeated hand use.

The solution must include a hands-free entry path that avoids manually opening Telegram and tapping the mic.

## Input Options (Ranked)

## Option A: Wake-Word Companion App (Best UX)
Add an Android companion listener (hotword button, hardware key, or always-listening mode) that captures speech and sends intent text directly to your assistant endpoint.

### Pros
- Eliminates Telegram input friction almost entirely.
- One command language (aligned with Talon phrases).
- Lowest interaction cost per command.

### Cons
- Highest implementation effort.
- Battery/privacy tuning required for always-listening mode.

## Option B: Quick Settings Tile + One-Tap Push-to-Talk
Companion app provides a Quick Settings tile that starts speech capture and posts to assistant.

### Pros
- Very fast to build.
- Much less hand strain than opening Telegram and keyboard.

### Cons
- Still requires one tap.

## Option C: Bluetooth Headset Button Trigger
Use headset media button (or accessibility remap) to trigger speech capture/send.

### Pros
- Near hands-free once configured.
- Works while screen is off in many cases.

### Cons
- Device/headset compatibility can vary.

## Option D: Keep Telegram, Remove Keyboard Dependence
Use Telegram voice-message flow only as fallback, not primary entry.

### Pros
- No new app required.

### Cons
- Still too much repetitive interaction for RSI over time.

## Options (Ranked)

## Option 1: Android Companion App + Accessibility Service (Best Long-Term)
Build a small Android app that receives commands from your existing assistant and executes actions locally.

### Pros
- Lowest ongoing latency once connected.
- Uses one command language (Talon-style intents).
- Can return structured success/failure feedback to Telegram.
- No root required.

### Cons
- Requires Android app development and permission onboarding.

### Typical command examples
- "open youtube"
- "search web for shoulder rehab exercises"
- "scroll down"
- "tap Subscribe"
- "type hello team"

## Option 2: ADB over Wi-Fi Bridge from PC (Fastest Prototype)
Use wireless debugging and have the existing .NET assistant run `adb shell` commands.

### Pros
- Fast to prototype (hours, not weeks).
- Good for launching apps, opening URLs, key events, taps/swipes.
- Can reuse existing assistant tooling on PC immediately.

### Cons
- Connection can break after reboots/network changes.
- Less semantic than Accessibility-based UI targeting.
- Requires developer options and pairing flow.

### Useful command types
- `am start` for apps/URLs
- `input keyevent` for navigation/media
- `input tap` and `input swipe` for touch simulation

## Option 3: Tasker + AutoInput + Telegram Trigger (Low-Code)
Wire Telegram/webhook triggers into Tasker tasks and AutoInput actions.

### Pros
- Quick no-code/low-code setup.
- Good for repetitive routines and app launching.

### Cons
- Macro maintenance overhead.
- Less robust for dynamic UIs than custom app logic.

## Recommended Architecture for This Repository
1. Split interaction into two channels:
   - command entry channel (hands-free input), and
   - execution/feedback channel (Telegram remains great for status and confirmations).
2. Add a command router in the assistant that can target:
    - PC executor (existing), or
    - Android executor (new).
3. Start Android executor as ADB bridge for immediate wins.
4. In parallel, add Android companion app for zero-touch command entry.
5. Evolve companion app with Accessibility Service for reliable intent-level actions.
6. Keep one shared command schema so Talon phrases, mobile speech entry, and Telegram text all map to the same intent names.

## Suggested Command Schema (Shared)
- `device.open_app(name)`
- `device.open_url(url)`
- `device.navigate(action)` where action in {back, home, recents, notifications}
- `device.scroll(direction)`
- `device.tap_text(text)`
- `device.type_text(text)`
- `device.media(action)` where action in {play, pause, next, previous}

## Latency/Usability Expectations
- Voice Access baseline: high latency and command mismatch.
- ADB bridge: usually low latency on same network, but occasional reconnect friction.
- Companion Accessibility app: best user experience once implemented and tuned.

## Security Notes
- Require explicit allowlist of executable commands.
- Reject unsafe free-form shell actions.
- Authenticate command source (chat identity + per-device token).
- Log every action and response for audit/debugging.

## Phased Plan

## Phase 0 (1-2 days): Remove Telegram input friction first
- Add a minimal Android companion app that records speech and POSTs command text to assistant.
- Provide at least one low-effort trigger: Quick Settings tile or persistent notification action.
- Keep Telegram as output/confirmation channel.
- Measure "time to first action" versus current Telegram mic workflow.

## Phase 1 (1-2 days): Prove feasibility
- Add Android target to assistant with ADB command execution.
- Implement 8-12 high-value commands (open app/url, nav, media, scroll).
- Measure average end-to-end latency from Telegram command to phone action.

## Phase 2 (1-2 weeks): Build reliable control path
- Create Android companion app (Kotlin) with Accessibility Service.
- Add Accessibility Service + minimal action engine.
- Support text-targeted taps and semantic navigation.
- Return structured status to assistant.

## Phase 3 (ongoing): Personalization and ergonomics
- Map Talon phrases to stable intent names.
- Add per-app command packs (YouTube, browser, messaging).
- Add fallback strategy when semantic action fails (retry with alternate selector).

## Practical Conclusion
This is feasible and worth doing. The key correction is to solve command entry ergonomics first. Best path: add zero-touch (or near zero-touch) mobile speech entry, then layer in ADB for fast control gains, and finally move to companion Accessibility execution for durable, low-latency phone control aligned with your Talon workflow.

 can we create a minimum viable product regarding the android companion at mentioned in the plan it doesn't have to do much just demonstrate the feasibility please do not pollute the personal assistant per se but create a separate project.  not sure how difficult it is to create this or  as for launching things I can always ask the assistant to provide me with a link and that works well in telegram chat on the phone it's the other stuff like dictating into text boxes and controlling the phone without actually using voice access all the time that we need to measure to see see if it's feasible.  I think I can work with opening the telegram app and dictating into it as a last result but if I'm doing that regularly on the phone it will make my repetitive strain injury worse.  we have already experimented with the adb shell and got that working but it relies on the phone being in debugging mode so is not really an option for non developers. 
    