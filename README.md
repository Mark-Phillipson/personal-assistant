# Personal Assistant (Copilot SDK + Telegram + Gmail, .NET 10)

This project is a personal assistant built on the GitHub Copilot SDK with two runtime transports:

- Telegram mode (long polling)
- CLI mode (single-prompt invocation)

## What it does

- In Telegram mode: receives Telegram messages via long polling, maintains one Copilot session per Telegram chat, forwards Telegram photos/documents to Copilot as attachments, and sends responses back to Telegram.
- In CLI mode: runs a single local request (use `--cli "<prompt>"` or `ASSISTANT_TRANSPORT=cli`).
- Supports `/start`, `/help`, `/reset`, and `/gmail-status`.
- Supports `/personality` to tune tone and emoji behavior per Telegram chat.
- Supports `/natural` and `/nc` to run local NaturalCommands CLI actions.
- Supports clipboard actions through a dedicated tool and `/clipboard-status` diagnostics.
- Can list and send local files from your user folders as Telegram attachments.
- Exposes Gmail tools to Copilot:
  - `gmail_setup_status`
  - `list_unread_gmail`
  - `read_gmail_message`
- Exposes a NaturalCommands tool to Copilot:
   - `run_natural_command`
- Exposes clipboard tools to Copilot:
   - `clipboard_setup_status`
   - `set_clipboard_text`
- Exposes browser playback tools to Copilot:
   - `open_in_default_browser`
   - `play_youtube_top_result`
   - `play_latest_youtube_podcast`
   - `open_spotify_search`
   - `play_latest_spotify_album`
   - `play_spotify_focus_music` — explicit Spotify focus music
   - `play_youtube_music_focus` — explicit YouTube Music focus music
   - `play_focus_music` — Spotify with automatic YouTube Music fallback
- Exposes Voice Admin launcher tools to Copilot (requires `VOICE_ADMIN_DB_PATH`, legacy fallback `VOICE_LAUNCHER_DB_PATH`):
   - `search_voice_admin_launchers` — keyword search across Name, CommandLine, and CategoryName
   - `launch_voice_admin_launcher` — start a launcher entry by its numeric ID
- Exposes Voice Admin todo tools to Copilot (requires `VOICE_ADMIN_DB_PATH`, legacy fallback `VOICE_LAUNCHER_DB_PATH`):
   - `list_voice_admin_open_todos` — list incomplete, non-archived todos with TodoId, title, project/category, priority, and created date
   - `add_voice_admin_todo` — add a new todo item (title required; optional description/project-category/priority)
   - `complete_voice_admin_todo` — mark a todo complete by TodoId
   - `complete_voice_admin_todo_by_text` — conversational shortcut to mark a todo complete by title/keyword (returns candidates when multiple match)
   - `assign_voice_admin_todo_project` — assign or clear project/category (stored in `Todos.Project`) for a todo by TodoId
   - `assign_voice_admin_todo_project_by_text` — conversational shortcut to assign/clear project/category by title/keyword (returns candidates when multiple match)
- Exposes read-only Voice Admin table search tools to Copilot (requires `VOICE_ADMIN_DB_PATH` or falls back to `VOICE_LAUNCHER_DB_PATH`):
   - `search_talon_commands` — keyword search in Talon Commands table
   - `get_talon_command_details` — fetch full Talon command details (including script) by RowId
   - `search_custom_in_tele_sense` — keyword search in Custom in Tele Sense table
   - `search_values_records` — keyword search in Values table
   - `search_transactions_records` — keyword search in Transactions table
   - `copy_voice_admin_value_to_clipboard` — read one value by table/row/column and copy it to clipboard
- Exposes read-only Talon user-directory file tools to Copilot:
   - `talon_user_directory_status` — verify Talon user directory availability and configured root
   - `list_talon_user_files` — list files under Talon user directory root (recursive by default)
   - `read_talon_user_file` — read file content from Talon user directory by relative path
   - `search_talon_user_files_text` — search text across Talon user directory files
   - `open_talon_user_directory_in_explorer` — open Windows File Explorer at the Talon user directory root or an optional relative subfolder
- Exposes allowlisted known-folder Explorer tools to Copilot:
   - `known_folder_explorer_status` — show configured folder roots for explorer actions
   - `open_known_folder_in_explorer` — open File Explorer at documents, desktop, downloads, pictures, videos, or repo (plus optional relative subfolder)

## Prerequisites

- .NET 10 SDK
- GitHub Copilot CLI installed and available as `copilot`
- Copilot CLI authenticated (or configured auth/BYOK)
- Telegram bot token from BotFather (required only for Telegram mode)
- For Gmail integration: Google Cloud OAuth client credentials for Gmail API

## Environment variables

- `ASSISTANT_TRANSPORT` (optional, default `telegram`; allowed: `telegram`, `cli`)
- `TELEGRAM_BOT_TOKEN` (required in `telegram` mode)
- `TELEGRAM_POLL_TIMEOUT_SECONDS` (optional, default `25`, range `1-50`)
- `TELEGRAM_ERROR_BACKOFF_SECONDS` (optional, default `3`, range `1-30`)
- `TELEGRAM_ATTACHMENT_STORAGE_PATH` (optional, default `%TEMP%\personal-assistant\telegram-attachments`)
- `TELEGRAM_ATTACHMENT_MAX_BYTES` (optional, default `20971520`, range `1024-52428800`)
- `GMAIL_CLIENT_SECRET_PATH` (optional, required only for Gmail; path to OAuth client secret JSON)
- `GMAIL_TOKEN_STORE_PATH` (optional, default `.gmail-token-store`)
- `GMAIL_EXPECTED_ACCOUNT_EMAIL` (optional, advisory/account hint)
- `CALENDAR_CLIENT_SECRET_PATH` (optional, required only for Google Calendar; path to OAuth client secret JSON)
- `CALENDAR_TOKEN_STORE_PATH` (optional, default `.calendar-token-store`)
- `CALENDAR_EXPECTED_ACCOUNT_EMAIL` (optional, advisory/account hint)
- `NATURAL_COMMANDS_EXECUTABLE` (optional, default `natural`; file path or command available in PATH)
- `NATURAL_COMMANDS_WORKING_DIRECTORY` (optional, working directory for NaturalCommands process)
- `NATURAL_COMMANDS_TIMEOUT_SECONDS` (optional, default `15`, range `1-120`)
- `VOICE_ADMIN_DB_PATH` (optional; full path to Voice Admin SQLite database; if unset, `VOICE_LAUNCHER_DB_PATH` is used as legacy fallback)
- `VOICE_ADMIN_MAX_RESULTS` (optional, default `20`, range `1-100`; maximum Voice Admin launcher and table-search results; legacy fallback reads `VOICE_LAUNCHER_MAX_RESULTS`)
- `VOICE_LAUNCHER_DB_PATH` (optional legacy fallback for `VOICE_ADMIN_DB_PATH`)
- `VOICE_LAUNCHER_MAX_RESULTS` (optional legacy fallback for `VOICE_ADMIN_MAX_RESULTS`)
- `ASSISTANT_TTS_ENABLED` (optional, default `false`; `true` to enable local preview speech)
- `ASSISTANT_TTS_PREVIEW_MAX_WORDS` (optional, default `40`; maximum spoken words)
- `ASSISTANT_TTS_PREFERRED_GENDER` (optional, default `male`; best-effort voice selection on Windows)
- `TALON_USER_DIRECTORY` (optional, default `%USERPROFILE%\AppData\Roaming\talon\user`; root path for read-only Talon file tools)
- `ASSISTANT_REPO_DIRECTORY` (optional, default current working directory when app starts; root path for the `repo` alias in `open_known_folder_in_explorer`)
- `UPWORK_CHROME_CDP_URL` (optional, default `http://127.0.0.1:9222`; when Chrome is started with remote debugging, Upwork tools can attach to your existing logged-in Chrome profile/session)
- `ASSISTANT_NAME` (optional, default `Bob`)
- `ASSISTANT_TONE` (optional, default `Friendly`; allowed: `Friendly`, `Professional`, `Witty`, `Calm`, `Irreverent`)
- `ASSISTANT_USE_EMOJI` (optional, default `true`; `true` or `false`)
- `ASSISTANT_EMOJI_DENSITY` (optional, default `Moderate`; allowed: `Subtle`, `Moderate`, `Expressive`)
- `ASSISTANT_SIGNATURE_GREETING` (optional)
- `ASSISTANT_SIGNATURE_FAREWELL` (optional)
- `ASSISTANT_PERSONALITY_CONFIG_PATH` (optional, default `personality.json`)

Use `.env.example` as a template.

If `personality.json` exists (or `ASSISTANT_PERSONALITY_CONFIG_PATH` points to a file), its values override environment defaults for startup personality.

Example `personality.json`:

```json
{
   "name": "Bob",
   "tone": "Friendly",
   "useEmoji": true,
   "emojiDensity": "Moderate",
   "signatureGreeting": "Hi there!",
   "signatureFarewell": "Talk soon"
}
```

## Gmail setup (for `MPhillipson0@gmail.com`)

1. Open Google Cloud Console and create/select a project.
2. Enable **Gmail API** for that project.
3. Configure the OAuth consent screen.
4. Create OAuth credentials of type **Desktop app**.
5. Download the OAuth client JSON and save it locally (for example `secrets\gmail-client-secret.json`).
6. Set `GMAIL_CLIENT_SECRET_PATH` to that file path.
7. Optionally set:
   - `GMAIL_EXPECTED_ACCOUNT_EMAIL=MPhillipson0@gmail.com`
   - `GMAIL_TOKEN_STORE_PATH=.gmail-token-store`
8. Start the assistant and ask it to list unread emails. The first Gmail call opens a browser for OAuth consent.

The app uses `gmail.readonly` scope only.

## Google Calendar setup

1. Open Google Cloud Console and create/select a project.
2. Enable **Google Calendar API** for that project.
3. Configure the OAuth consent screen.
4. Create OAuth credentials of type **Desktop app**.
5. Download the OAuth client JSON and save it locally (for example `secrets\calendar-client-secret.json`).
6. Set `CALENDAR_CLIENT_SECRET_PATH` to that file path.
7. Optionally set:
   - `CALENDAR_EXPECTED_ACCOUNT_EMAIL=your@email.com`
   - `CALENDAR_TOKEN_STORE_PATH=.calendar-token-store`
8. Start the assistant and use `/calendar-status` or `/calendar-events`. The first Calendar call opens a browser for OAuth consent.

The app uses `calendar` scope only.

## Run

### Telegram mode (default)

```bash
dotnet restore
dotnet build
dotnet run
```

On startup, the app begins polling Telegram updates and routes each chat to its own Copilot session. Telegram text, captions, photos, and documents are accepted. Photos and documents are downloaded to a local temp/cache folder and sent to Copilot as message attachments.

### CLI mode (single-prompt)

Run a single prompt and exit. Use the `--cli` flag or set `ASSISTANT_TRANSPORT=cli`.

Startup flag example:

```bash
dotnet run -- --cli "bob please play Ukraine the latest podcast"
```

Environment variable example:

```bash
ASSISTANT_TRANSPORT=cli
dotnet run -- --cli "bob please play Ukraine the latest podcast"
```

## NaturalCommands setup

1. Ensure the NaturalCommands CLI is installed and runnable from the command line.
2. Set `NATURAL_COMMANDS_EXECUTABLE` if the command is not `natural` or not on your PATH.
3. Recommended on this machine: `NATURAL_COMMANDS_EXECUTABLE=C:\Users\MPhil\source\repos\NaturalCommands\bin\Debug\net10.0-windows\NaturalCommands.exe`
4. Optionally set `NATURAL_COMMANDS_WORKING_DIRECTORY` if commands depend on a specific folder.
5. Keep or adjust `NATURAL_COMMANDS_TIMEOUT_SECONDS` (default 15 seconds).
6. Restart the assistant.

## Spotify notes

- The current Spotify integration in this project is browser-based, not OAuth/Web API playback control.
- It can open Spotify search and album pages for prompts like `Play the latest album from Metallica on Spotify`.
- It can also open lyric-free focus music results for prompts like `Play music to code by` or `Play contemplation music`. Spotify is tried first, with automatic YouTube Music fallback if needed.
- Per Spotify's current developer docs, official Web API and Web Playback SDK usage require a Premium account, so this project does not try to control Spotify playback through the API on a free account.
- Actual playback behavior still depends on your logged-in Spotify browser/app session and any free-tier playback restrictions.

## Telegram usage examples

- `/gmail-status`
- `Check my unread Gmail emails`
- `Read the first unread email and summarize it`
- `/calendar-status`
- `/calendar-events`
- `/calendar-create`
- `/natural show desktop`
- `/nc show desktop`
- `/clipboard-status`
- `Copy this exact text to my clipboard: Hello from Bob`
- `What is in this screenshot?` with a screenshot attached
- `Summarize this PDF` with a PDF attached
- `/personality emoji expressive`
- `/personality tone calm`
- `List my next 5 calendar events`
- `Create a calendar event titled Meeting tomorrow at 10am for 1 hour`
- `List files in my Downloads folder`
- `Find PDFs in Documents and send budget-2026.pdf`
- `Show recent videos in Videos and send trip.mp4`
- `Play the latest Ukraine podcast on YouTube`
- `Play the latest Linus Tech Tips video on YouTube`
- `Play the latest album from Metallica on Spotify`
- `Open Spotify search for Master of Puppets`
- `Play music to code by` (Spotify → YouTube Music fallback)
- `Play music to code by on Spotify` (explicit Spotify)
- `Play music to code by on YouTube Music` (explicit YouTube Music)
- `Play contemplation music without lyrics`
- `List Talon Commands with Upwork`
- `Find Custom in Tele Sense entries containing Blazor`
- `Search Values for customerId`
- `List Transactions containing invoice`
- `Copy the Value column from Values row 42 to my clipboard`
- `Open my Talon user folder in File Explorer`
- `Open my Documents folder in File Explorer`
- `Open Downloads\Invoices in File Explorer`
- `Open the repo folder in File Explorer`

## Upwork browser-assisted reply flow (draft-first)

This is an assistive workflow for an already authenticated user session. It is not a bypass flow and should only be used with explicit user review before send.

Prerequisites:

- Preferred: run Chrome with remote debugging so Upwork tools can reuse your existing logged-in session/tab.
- Fallback: Upwork tools auto-open a visible automation browser window for the session.
- `PLAYWRIGHT_HEADLESS` can remain true for other browser tools.
- Use the new Upwork tools through normal assistant prompts.

Optional one-time setup for existing Chrome session reuse (Windows example):

```powershell
"C:\Program Files\Google\Chrome\Application\chrome.exe" --remote-debugging-port=9222
```

If using a non-default endpoint, set `UPWORK_CHROME_CDP_URL` accordingly.

If Upwork opens in the fallback automation browser, rerun `Open Upwork messages portal` after starting Chrome in remote-debug mode and check console logs for `[upwork.browser]` CDP attach status.

Recommended sequence:

1. Open and authenticate session context:
   - `Open Upwork messages portal`
2. Confirm session is ready:
   - `Check Upwork session status`
3. Read current room context:
   - `Read the current Upwork room and pull the latest 8 messages`
4. Draft from rough intent:
   - `Reply to this room saying roughly that I can do the change, will deliver tomorrow, and ask if they want EN and FR labels exactly as discussed`
5. Send only on explicit confirmation:
   - `Send that now`

Safety behavior:

- The assistant should draft into the composer first (`sendNow=false`).
- The assistant should only attempt send after you explicitly confirm in that turn.

## Local attachment behavior

The assistant can attach existing local files from these folders (when they exist on the machine running the bot):

- `Documents`
- `Pictures`
- `Videos`
- `Desktop`
- `Downloads`

## Telegram incoming attachments

- Incoming Telegram `photo` and `document` messages are downloaded locally, attached to the Copilot request, and deleted after the request finishes.
- If the message has a caption, that caption is used as the prompt alongside the attachment.
- If the message has no caption, the assistant asks Copilot to inspect the attachment and describe it.
- Files larger than `TELEGRAM_ATTACHMENT_MAX_BYTES` are rejected before they are sent to Copilot.

For safety, it only allows files inside those folders (including subfolders when requested), blocks path traversal outside the allowed roots, and applies a local max file size cap (48 MB).
