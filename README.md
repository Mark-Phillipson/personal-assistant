# Personal Assistant (Copilot SDK + Telegram/Terminal + Gmail, .NET 10)

This project is a personal assistant built on the GitHub Copilot SDK with two runtime transports:

- Telegram mode (long polling)
- Terminal mode (direct interactive chat in your shell)

## What it does

- In Telegram mode: receives Telegram messages via long polling, maintains one Copilot session per Telegram chat, and sends responses back to Telegram.
- In terminal mode: runs as an interactive shell assistant with one local Copilot session.
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
- Exposes Voice Admin launcher tools to Copilot (requires `VOICE_ADMIN_DB_PATH`, legacy fallback `VOICE_LAUNCHER_DB_PATH`):
   - `search_voice_admin_launchers` — keyword search across Name, CommandLine, and CategoryName
   - `launch_voice_admin_launcher` — start a launcher entry by its numeric ID
- Exposes read-only Voice Admin table search tools to Copilot (requires `VOICE_ADMIN_DB_PATH` or falls back to `VOICE_LAUNCHER_DB_PATH`):
   - `search_talon_commands` — keyword search in Talon Commands table
   - `get_talon_command_details` — fetch full Talon command details (including script) by RowId
   - `search_custom_in_tele_sense` — keyword search in Custom in Tele Sense table
   - `search_values_records` — keyword search in Values table
   - `search_transactions_records` — keyword search in Transactions table
   - `copy_voice_admin_value_to_clipboard` — read one value by table/row/column and copy it to clipboard

## Prerequisites

- .NET 10 SDK
- GitHub Copilot CLI installed and available as `copilot`
- Copilot CLI authenticated (or configured auth/BYOK)
- Telegram bot token from BotFather (required only for Telegram mode)
- For Gmail integration: Google Cloud OAuth client credentials for Gmail API

## Environment variables

- `ASSISTANT_TRANSPORT` (optional, default `telegram`; allowed: `telegram`, `terminal`)
- `TELEGRAM_BOT_TOKEN` (required in `telegram` mode)
- `TELEGRAM_POLL_TIMEOUT_SECONDS` (optional, default `25`, range `1-50`)
- `TELEGRAM_ERROR_BACKOFF_SECONDS` (optional, default `3`, range `1-30`)
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

On startup, the app begins polling Telegram updates and routes each chat to its own Copilot session.

### Terminal mode (no Telegram required)

Use either an environment variable:

```bash
ASSISTANT_TRANSPORT=terminal
dotnet run
```

Or use a startup flag:

```bash
dotnet run -- --terminal
```

Terminal commands:

- `/help`
- `/reset`
- `/gmail-status`
- `/calendar-status`
- `/clipboard-status`
- `/natural <command>`
- `/nc <command>`
- `/personality emoji on|off|subtle|moderate|expressive`
- `/personality tone friendly|professional|witty|calm|irreverent`
- `/personality reset`
- `/exit`

## NaturalCommands setup

1. Ensure the NaturalCommands CLI is installed and runnable from the command line.
2. Set `NATURAL_COMMANDS_EXECUTABLE` if the command is not `natural` or not on your PATH.
3. Recommended on this machine: `NATURAL_COMMANDS_EXECUTABLE=C:\Users\MPhil\source\repos\NaturalCommands\bin\Debug\net10.0-windows\NaturalCommands.exe`
4. Optionally set `NATURAL_COMMANDS_WORKING_DIRECTORY` if commands depend on a specific folder.
5. Keep or adjust `NATURAL_COMMANDS_TIMEOUT_SECONDS` (default 15 seconds).
6. Restart the assistant.

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
- `/personality emoji expressive`
- `/personality tone calm`
- `List my next 5 calendar events`
- `Create a calendar event titled Meeting tomorrow at 10am for 1 hour`
- `List files in my Downloads folder`
- `Find PDFs in Documents and send budget-2026.pdf`
- `Show recent videos in Videos and send trip.mp4`
- `Play the latest Ukraine podcast on YouTube`
- `Play the latest Linus Tech Tips video on YouTube`
- `List Talon Commands with Upwork`
- `Find Custom in Tele Sense entries containing Blazor`
- `Search Values for customerId`
- `List Transactions containing invoice`
- `Copy the Value column from Values row 42 to my clipboard`

## Local attachment behavior

The assistant can attach existing local files from these folders (when they exist on the machine running the bot):

- `Documents`
- `Pictures`
- `Videos`
- `Desktop`
- `Downloads`

For safety, it only allows files inside those folders (including subfolders when requested), blocks path traversal outside the allowed roots, and applies a local max file size cap (48 MB).
