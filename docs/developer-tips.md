# Developer Tips Announcer

This feature announces developer tips periodically and allows Telegram chats to opt-in.

Files
- `documents/developer-tips.json` — list of tips. Each tip should be an object with `id`, `text`, and `tags` (allowed tags: `dotnet`, `general`).
- State file: `%APPDATA%/personal-assistant/dev-tips-state.json` stores subscribers and settings.

Telegram commands
- `/devtips on [category]` — subscribe this chat; `category` is `general` or `dotnet` (default: `general`).
- `/devtips off` — unsubscribe this chat.
- `/devtips status` — show subscription status for this chat.
- `/devtips now` — force an immediate tip for this chat.
- `/devtips mode [dotnet|general]` — set preferred tip category for this chat.
- `/devtips audio [on|off]` — enable/disable WAV audio delivery for this chat (audio requires TTS enabled and Azure speech credentials).

Notes
- By default, Telegram subscribers receive text messages. Audio delivery is optional per subscriber.
- Tips are filtered by tag to avoid non-.NET language tips.
