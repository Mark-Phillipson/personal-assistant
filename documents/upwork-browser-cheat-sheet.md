# Upwork Browser-Assist Cheat Sheet

Date: 2026-03-16
Branch: feature/upwork-browser-current-room-reply

## Purpose
Use a logged-in browser session to:
- open Upwork messages,
- read the current room context,
- draft a reply from rough guidance,
- send only after explicit confirmation.

## Prerequisites
- Best reliability: run Chrome with remote debugging so tools can reuse your existing logged-in profile/session:
   - `"C:\Program Files\Google\Chrome\Application\chrome.exe" --remote-debugging-port=9222`
- Optional: set `UPWORK_CHROME_CDP_URL` if your endpoint is not `http://127.0.0.1:9222`.
- Fallback behavior: Upwork tools auto-open a visible automation browser window if CDP is unavailable.
- You do not need to disable global headless mode for other browser tools.
- Use the same visible automation browser window for the full flow.

## Fast Flow (copy-ready prompts)
1. Open portal:
   - `Open Upwork messages portal.`
2. Check status:
   - `Check Upwork session status.`
3. Read current room:
   - `Read the current Upwork room and capture the latest 8 messages.`
4. Draft from rough intent:
   - `Reply to this room saying roughly that I can do this update, deliver by tomorrow, and ask them to confirm EN/FR label wording.`
5. Refine tone/length:
   - `Make it shorter and more direct.`
6. Send explicitly:
   - `Send that now.`

## Useful Draft Prompts
- `Write this as professional but friendly.`
- `Keep it under 3 sentences.`
- `Add one clear next step and a timeline.`
- `Ask one clarifying question about scope before I send.`
- `Give me 3 alternatives: concise, neutral, and warm.`

## Safe Operating Rules
- Draft-first is default.
- Do not send unless you explicitly confirm in the same turn.
- If context looks incomplete, ask to re-read the room before drafting.
- If the browser is on login, sign in there first, then retry read/draft.

## Troubleshooting
- If session status says no active page:
  - `Open Upwork messages portal.`
- If it opens the Playwright browser instead of your Chrome session:
   - Start Chrome with `--remote-debugging-port=9222`.
   - Run `Open Upwork messages portal.` again (CDP attach is retried automatically).
   - Check console logs for `[upwork.browser]` lines showing CDP attach success/failure.
- If the room is not detected:
  - Open a specific room in the same browser tab, then run read again.
- If send button is not found:
  - Use draft mode and click Send manually in the browser.

## Suggested Pilot Sequence
1. Run one low-risk thread in draft-only mode.
2. Validate context quality and draft usefulness.
3. Use explicit-send for a real reply only after manual review.