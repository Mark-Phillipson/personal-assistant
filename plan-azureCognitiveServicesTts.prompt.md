# Plan: Upgrade TTS to Azure Cognitive Services Speech

Replace the robotic `System.Speech` (Windows SAPI) voices with Azure Cognitive Services Speech neural voices. Azure-only, no System.Speech fallback. Use `AZURE_SPEECH_KEY` (already configured as a user env var) with region `uksouth`. Default to `en-GB-RyanNeural` — a natural-sounding UK English male voice.

---

## Phase 1: Add Azure Speech SDK dependency

1. Add NuGet package `Microsoft.CognitiveServices.Speech` to `personal-assistant.csproj`
2. Remove `System.Speech` package reference (no longer needed)

## Phase 2: Add environment variables *(parallel with Phase 1)*

3. Add `AZURE_SPEECH_KEY`, `AZURE_SPEECH_REGION=uksouth` to `.env`
4. Add `AZURE_SPEECH_VOICE=en-GB-RyanNeural` env var for voice selection flexibility

## Phase 3: Rewrite TextToSpeechService.cs

5. Replace the `System.Speech.Synthesis` implementation in `TextToSpeechService.cs` with Azure Cognitive Services Speech SDK:
   - Swap `using System.Speech.Synthesis` for `using Microsoft.CognitiveServices.Speech` + `Microsoft.CognitiveServices.Speech.Audio`
   - Constructor takes `string? azureKey, string? azureRegion, string voiceName`
   - `FromEnvironment()` reads `AZURE_SPEECH_KEY`, `AZURE_SPEECH_REGION`, `AZURE_SPEECH_VOICE` (default `en-GB-RyanNeural`)
   - `TrySpeakPreviewAsync()` creates `SpeechConfig.FromSubscription(key, region)`, sets `SpeechSynthesisVoiceName`, uses `AudioConfig.FromDefaultSpeakerOutput()`, calls `SpeakTextAsync(snippet)`
   - Remove `[SupportedOSPlatform("windows")]` attribute and `RuntimeInformation` platform check (Azure SDK is cross-platform)
   - Remove WAV file fallback creation (was a System.Speech workaround)
   - Keep all shared helpers unchanged: `Log()`, `IsLikelyTableContent()`, `ExtractPreviewText()`, `StripHtmlTags()`
   - If `AZURE_SPEECH_KEY` is not set, log warning and silently skip TTS

## Phase 4: Clean up

6. Check if `CA1416` in `<NoWarn>` is still needed for anything else — if not, remove it from `personal-assistant.csproj`
7. Update `.env` comments to document the new Azure Speech variables

---

## Relevant files

- `personal-assistant.csproj` — Swap NuGet packages
- `TextToSpeechService.cs` — Core rewrite (keep `Log`, `IsLikelyTableContent`, `ExtractPreviewText`, `StripHtmlTags`)
- `.env` — Add `AZURE_SPEECH_KEY`, `AZURE_SPEECH_REGION`, `AZURE_SPEECH_VOICE`
- **No changes needed**: `Program.cs`, `TelegramMessageHandler.cs`, `EnvironmentSettings.cs` — public API (`FromEnvironment()`, `TrySpeakPreviewAsync`) stays identical

## Verification

1. **Build**: `dotnet build -p:UseAppHost=false -p:OutDir=bin\Debug\net10.0-verify\`
2. **TTS test**: Run `tts test` command — should hear natural Ryan neural voice through speakers
3. **Missing key test**: Unset `AZURE_SPEECH_KEY`, confirm TTS silently skips without crashing
4. **Log check**: Inspect `tts-debug.log` for Azure synthesis success/failure entries
5. **Table filter**: Send a table-formatted response and confirm TTS is still skipped for tables

## Decisions

- **No System.Speech fallback** — Azure-only per user preference; TTS silently skips if Azure unavailable
- **Default voice**: `en-GB-RyanNeural` (UK English male, natural-sounding, matches `uksouth` region)
- **Configurable** via `AZURE_SPEECH_VOICE` env var — can switch to `en-US-GuyNeural`, `en-US-DavisNeural`, `en-AU-WilliamNeural`, etc. without code changes
- **Cross-platform**: Removing System.Speech makes TTS work on Linux/macOS too
- **Keep `ASSISTANT_TTS_ENABLED`** as master on/off toggle
- **Cost**: Azure free tier = 500K chars/month neural; the 40-word preview truncation keeps usage minimal

## Further Considerations

1. **Voice exploration**: Azure has 400+ neural voices — browse at the Azure Speech Studio voice gallery. Since you're in UK South, `en-GB-RyanNeural` is a great starting point.
2. **SSML support**: Could be added later for expressive speech (emphasis, pauses, emotion) — out of scope for now.
