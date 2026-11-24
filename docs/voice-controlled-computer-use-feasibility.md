# Voice-controlled computer use: feasibility and implementation decision

**Status:** proof-of-concept hardening in progress  
**Repository issue:** [#18](https://github.com/Mark-Phillipson/personal-assistant/issues/18)  
**Current project SDK reference:** `GitHub.Copilot.SDK` 1.0.23 in `personal-assistant.csproj`

## Decision

Keep GitHub Copilot SDK as Bob's orchestration and custom-tool runtime. Keep the existing Windows automation implementation as a deliberately narrow, host-local tool provider. Do **not** replace the Copilot SDK with the Codex SDK.

The Copilot SDK supports custom tools, session permission handling, and lifecycle hooks. That is sufficient to route a voice-originated request to the existing C# services and to add application-owned policy around tool execution. The SDK is an integration layer for Copilot CLI; its documented .NET surface does not provide a built-in screenshot/vision desktop driver that can independently see and operate the Windows desktop.

OpenAI's computer-use capability is a separate Responses API integration. The application supplies a computer driver that captures screenshots and executes model-requested actions. It could be evaluated later as an optional vision fallback, but it would introduce a second model/API, separate state and billing, a driver implementation, and additional privacy and permission work. The Codex SDK is for programmatically controlling local Codex coding agents; it is not a drop-in general-purpose desktop-control SDK for Bob.

## Existing capability

The repository already contains the main building blocks:

- `ComputerToolsService`: allowlisted folder aliases, file listing/search, opening folders/files, and completion notification.
- `DesktopAutomationService`: visible-window discovery, active-window inspection, focus, mouse movement/click/scroll, text input, and key combinations through Windows APIs.
- `AssistantToolsFactory`: registers the computer and desktop services as Copilot tools.
- `Program.cs`: constructs the services and includes `--desktop-tools-smoke` and `--computer-tools-smoke` diagnostic paths.
- Telegram and Android companion input paths already forward natural-language requests into Bob. CLI mode is also available for local diagnostics.

The missing work is not a new SDK. It is controlled activation, end-to-end validation from the real voice input path, and reliable user feedback.

## Changes in this branch

1. Desktop input is now **disabled by default**. Read-only window inspection remains available.
2. Set `DESKTOP_AUTOMATION_ENABLED=true` in the host process environment and restart Bob to enable focus, mouse, keyboard, typing, and scrolling actions.
3. Enabled input operations write a timestamped action event to the application console log. Typed content is deliberately not logged.
4. A single text-input action is limited to 4,000 characters and a key sequence to 200 characters.

This opt-in is a coarse safety switch, not a substitute for per-action approval. It must not be treated as sufficient protection for purchases, sending messages, publishing, deleting data, or other consequential actions. Those actions should use dedicated application APIs with explicit confirmation rather than screen automation.

## Runtime and availability constraints

- Desktop automation must run in the interactive Windows user session that owns the desktop. Running the process as a Windows service or in Session 0 will not provide normal access to the signed-in user's desktop.
- For a request initiated while away from the computer, the host must remain powered on, Bob must remain running, and the voice/Telegram/companion transport must be reachable.
- Lock screen, UAC secure desktop, disconnected sessions, display changes, focus restrictions, and applications that do not expose useful accessibility information can prevent or undermine automation.
- A successful `SendInput` call means Windows accepted the input event; it does not prove the intended application completed the task. Verify observable state before reporting success.
- Prefer deterministic tools first: file APIs, app-specific APIs, Playwright/browser DOM operations, NaturalCommands, and existing launcher integrations. Use coordinates only as a fallback.

## SDK and provider findings

- The project currently pins `GitHub.Copilot.SDK` 1.0.23. No package upgrade is made in this change: the required capabilities (custom tools and permission handling) already exist, and a version bump without a demonstrated missing API would add risk without solving the absence of a native desktop-vision driver.
- The Copilot SDK .NET documentation describes custom tool registration, permission handlers/modes, and session hooks. A pre-tool hook can deny or constrain tool calls; permission handlers can approve or deny runtime permission requests. These should be used for future fine-grained approvals and audit integration.
- OpenAI computer use is exposed through the Responses API and requires the application to implement or connect a driver for screenshots and desktop actions. It is a viable separate experiment if visual grounding is needed, not a capability unlocked by changing the Copilot SDK package.
- The Codex SDK targets local Codex agent workflows and app-server integration. It is not the recommended replacement for Bob's current Copilot SDK session.

References (official documentation):

- [GitHub Copilot SDK repository and FAQ](https://github.com/github/copilot-sdk)
- [Copilot SDK for .NET](https://github.com/microsoft/copilot-sdk/blob/main/dotnet/README.md)
- [Copilot SDK session hooks](https://github.com/github/copilot-sdk/blob/main/docs/features/hooks.md)
- [OpenAI computer-use guide](https://developers.openai.com/api/docs/guides/tools-computer-use)
- [OpenAI computer-use driver contract](https://developers.openai.com/api/docs/guides/tools-computer-use-sdk-drivers)
- [Codex SDK](https://learn.chatgpt.com/docs/codex-sdk)

## Repeatable end-to-end test plan

Run these tests on the Windows host in the same interactive session as Bob. Start with the input gate disabled.

| Test | Voice request | Expected result |
|---|---|---|
| Read-only discovery | “Bob, list the visible desktop windows.” | Window titles/process names returned; no input injected. |
| Disabled input | “Bob, focus Microsoft Edge.” | Clear message that desktop input is disabled; no focus change. |
| Enable and focus | Set `DESKTOP_AUTOMATION_ENABLED=true`, restart Bob, then ask “Bob, focus Microsoft Edge.” | Edge is focused if Windows permits it; result states if focus was blocked. |
| Open folder | “Bob, open my Downloads folder.” | File Explorer opens at Downloads using the dedicated folder tool. |
| Browser navigation | “Bob, open Edge and open https://example.com.” | Prefer the existing browser tool; verify the page actually loaded. |
| Type text | “Bob, type this harmless test phrase into the active Notepad window: voice test.” | Only after the explicit opt-in; verify the exact text in Notepad. |
| Cancellation | Cancel the request or stop Bob during a longer operation. | The request stops or reports that the current synchronous Windows input call cannot be interrupted. |

Also run:

```powershell
dotnet run -- --desktop-tools-smoke
```

This diagnostic only reports status and reads visible-window information. The existing `--computer-tools-smoke` test creates files and launches Explorer/default applications; run it only when that side effect is intended.

## Current acceptance status

- [x] Document the Copilot SDK versus Codex/OpenAI computer-use decision.
- [x] Confirm that the currently referenced Copilot SDK already supports custom tools and permission handling.
- [x] Add a default-off switch for physical desktop input and action-level console logging.
- [ ] Run a Windows .NET build and existing tests.
- [ ] Execute at least three low-risk actions end to end from voice input on the user's host.
- [ ] Validate confirmation, cancellation, timeout, and notification behavior in the live Telegram/companion workflow.
- [ ] Add fine-grained per-action approval and durable audit storage before enabling consequential workflows.

## Feasibility conclusion

The existing architecture is viable for nearby and remote-triggered low-risk tasks, provided Bob runs in the user's interactive Windows session. Remote triggering does not mean remote desktop availability: the host must be on and logged in, and screen automation can be blocked by Windows security boundaries or focus state.

The best next increment is to validate the existing dedicated tools from voice input, then add a real confirmation/cancellation broker for any consequential action. Only investigate a Responses API computer-vision driver if the deterministic tools and existing Windows automation cannot reliably identify the target.
