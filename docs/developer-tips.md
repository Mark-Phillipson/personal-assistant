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

- `/devtips schedule <show|hourly|fixed <minutes>|times <hh:mm[,hh:mm]>|random <min> <max>` — configure when tips are announced. Examples:
	- `/devtips schedule show` — show current schedule.
	- `/devtips schedule hourly` — announce at the top of each hour.
	- `/devtips schedule fixed 30` — announce every 30 minutes (interval-based).
	- `/devtips schedule times 09:15,18:30` — announce at 09:15 and 18:30 local time each day.
	- `/devtips schedule random 15 120` — announce at random intervals between 15 and 120 minutes.

Notes
- By default, Telegram subscribers receive text messages. Audio delivery is optional per subscriber.
- Tips are filtered by tag to avoid non-.NET language tips.
