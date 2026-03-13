# Personality & Emotion Plan

Give the assistant a consistent personality with configurable tone, and add optional emoji/emotion icon support in its responses.

---

## Step 1 — Define a Personality Profile

Create a `PersonalityProfile` class (or record) that holds:

- `Name` — the assistant's chosen name (e.g. "Bob")
- `Tone` — an enum: `Friendly`, `Professional`, `Witty`, `Calm`, `Irreverent`
- `UseEmoji` — `bool` to enable or disable emoji in responses
- `EmojiDensity` — an enum: `Subtle`, `Moderate`, `Expressive`
- `SignatureGreeting` / `SignatureFarewell` — optional personalised phrases

Load the profile from environment variables or a `personality.json` config file so it can be changed without recompiling.

Environment variables to add:
```
ASSISTANT_NAME=Bob
ASSISTANT_TONE=Friendly
ASSISTANT_USE_EMOJI=true
ASSISTANT_EMOJI_DENSITY=Moderate
```

---

## Step 2 — Build the System Prompt

Create a `SystemPromptBuilder` class that constructs the Copilot system prompt from the `PersonalityProfile`.

Example output for a Friendly + Moderate-emoji profile:
```
You are Bob, a warm and friendly personal assistant. You are helpful, concise,
and occasionally use emoji to add warmth to your replies 😊. Adjust your emoji
use to suit the context — more playful for casual chat, more restrained for
emails and calendar tasks.
```

Key rules to encode in the prompt:
- Never use emoji inside email drafts, calendar descriptions, or code snippets.
- Emoji should enhance tone, not replace words.
- Match the user's energy — if they write formally, dial back emoji.
- Use contextual emoji (✅ for confirmations, 📅 for calendar, 📧 for email, ⚠️ for warnings).

---

## Step 3 — Wire the System Prompt into Copilot Sessions

Update `TelegramMessageHandler` (and the terminal runner in `Program.cs`) to:

1. Instantiate `PersonalityProfile` from environment on startup.
2. Pass it to a `SystemPromptBuilder.Build(profile)` call.
3. Set the resulting string as the system prompt when creating `CopilotSession` objects in `GetOrCreateSessionAsync`.

---

## Step 4 — Add an Emoji Toggle Command

Add a `/personality` Telegram command so the user can adjust settings at runtime without restarting the bot:

```
/personality emoji on        — enable emoji
/personality emoji off       — disable emoji
/personality emoji subtle    — low-density emoji
/personality emoji expressive — high-density emoji
/personality tone witty      — switch tone
/personality reset           — restore defaults from environment
```

Store the per-chat overrides in the existing `ConcurrentDictionary<long, CopilotSession>` keyed by `chatId`, or in a parallel `ConcurrentDictionary<long, PersonalityProfile>`.

---

## Step 5 — Contextual Emoji Helpers

Create a small static `EmojiPalette` class with contextual constants and helper methods:

```csharp
internal static class EmojiPalette
{
    public const string Calendar   = "📅";
    public const string Email      = "📧";
    public const string Confirm    = "✅";
    public const string Warning    = "⚠️";
    public const string Thinking   = "🤔";
    public const string Happy      = "😊";
    public const string Wave       = "👋";
    public const string Rocket     = "🚀";
    public const string Search     = "🔍";
    public const string Music      = "🎵";   // future: podcast/YouTube feature
    public const string Lock       = "🔒";

    public static string Wrap(string text, string emoji, bool useEmoji)
        => useEmoji ? $"{emoji} {text}" : text;
}
```

Use `EmojiPalette` in hard-coded bot responses (e.g. `/start`, `/help`, command confirmations) so they respect the `UseEmoji` flag.

---

## Step 6 — Update Hard-Coded Responses

Audit all `SendMessageInChunksAsync` calls in `TelegramMessageHandler.cs` and update them to use `EmojiPalette.Wrap` or inline the emoji behind the `UseEmoji` flag, for example:

```
/start  →  "👋 Hi! I'm Bob, your personal assistant. ..."
/help   →  "📋 Commands: ..."  (each command prefixed with a contextual icon)
errors  →  "⚠️ Something went wrong: ..."
```

---

## Step 7 — Add Personality Status to `/help`

Extend the `/help` response to show the current personality settings:

```
🤖 Personality: Bob | Tone: Friendly | Emoji: Moderate
```

---

## Step 8 — Persist Per-Chat Preferences (Optional / Later)

If per-user personality overrides should survive bot restarts:

- Serialize `ConcurrentDictionary<long, PersonalityProfile>` to a local JSON file (`personality_state.json`).
- Load it on startup.
- Save it on each change.
- Protect access with a `SemaphoreSlim` to avoid file-write races.

---

## File Checklist

| File | Change |
|---|---|
| `PersonalityProfile.cs` | New — profile record/class |
| `SystemPromptBuilder.cs` | New — builds system prompt string |
| `EmojiPalette.cs` | New — emoji constants + `Wrap` helper |
| `EnvironmentSettings.cs` | Add reads for new env vars |
| `TelegramMessageHandler.cs` | Wire profile, add `/personality` command, update hard-coded strings |
| `Program.cs` | Pass profile through to session creation |
| `AssistantToolsFactory.cs` | No changes needed |
| `README.md` | Document new env vars and `/personality` command |

---

## Notes

- Emoji support in Telegram is native — no special encoding is needed; just include the Unicode character in the string.
- Copilot model responses already support Unicode, so no changes are needed in the SDK wiring.
- Keep emoji out of log output — strip them before writing to `Console` if log readability matters.
