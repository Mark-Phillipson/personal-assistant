# Personal Assistant (Copilot SDK + Telegram + Gmail, .NET 10)

This project is a Telegram-based personal assistant built on the GitHub Copilot SDK, with optional Gmail read access.

## What it does

- Receives Telegram messages via long polling.
- Maintains one Copilot session per Telegram chat.
- Sends Copilot responses back to Telegram.
- Supports `/start`, `/help`, `/reset`, and `/gmail-status`.
- Supports `/natural` and `/nc` to run local NaturalCommands CLI actions.
- Can list and send local files from your user folders as Telegram attachments.
- Exposes Gmail tools to Copilot:
  - `gmail_setup_status`
  - `list_unread_gmail`
  - `read_gmail_message`
- Exposes a NaturalCommands tool to Copilot:
   - `run_natural_command`

## Prerequisites

- .NET 10 SDK
- GitHub Copilot CLI installed and available as `copilot`
- Copilot CLI authenticated (or configured auth/BYOK)
- Telegram bot token from BotFather
- For Gmail integration: Google Cloud OAuth client credentials for Gmail API

## Environment variables

- `TELEGRAM_BOT_TOKEN` (required)
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

Use `.env.example` as a template.

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

```bash
dotnet restore
dotnet build
dotnet run
```

On startup, the app begins polling Telegram updates and routes each chat to its own Copilot session.

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
- `List my next 5 calendar events`
- `Create a calendar event titled Meeting tomorrow at 10am for 1 hour`
- `List files in my Downloads folder`
- `Find PDFs in Documents and send budget-2026.pdf`
- `Show recent videos in Videos and send trip.mp4`

## Local attachment behavior

The assistant can attach existing local files from these folders (when they exist on the machine running the bot):

- `Documents`
- `Pictures`
- `Videos`
- `Desktop`
- `Downloads`

For safety, it only allows files inside those folders (including subfolders when requested), blocks path traversal outside the allowed roots, and applies a local max file size cap (48 MB).
