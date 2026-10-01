## Plan: First POC for five computer tools

TL;DR: build a minimal, reliable first proof-of-concept that lets the Copilot agent operate on the local Windows filesystem through a small set of explicit tools: ListFiles, SearchFiles, OpenFolder, OpenFile, and NotifyUser. Keep the scope to deterministic file access and user notifications; do not add browser automation, keyboard control, or general screen-clicking in this first milestone.

**Steps**
1. Create a small Windows computer-tools service that owns the actual file and notification logic and keeps the AI layer thin. This service should centralize paths, file filtering, and safe defaults so the model only calls commands that are explicitly supported. Depends on no other step.
2. Add the first five AIFunction tools in AssistantToolsFactory.cs, wired to the new service. Use natural, model-friendly names and descriptions so the model can decide when to call them without hard-coded command matching. Suggested tools: list_files_in_folder, find_files_by_pattern, open_folder, open_file, notify_user.
3. Add a minimal execution path in Program.cs that starts the session with these five tools enabled and keeps the rest of the assistant unchanged. This should reuse the existing Copilot session setup pattern already used by the project instead of introducing a second runtime.
4. Keep the POC intentionally limited to the user’s local machine: Downloads, Documents, obvious known paths, and optionally a UserProfile root. Add safe validation for folder existence, missing folders, and file-size limits before opening or returning file results.
5. Add a completion/notification flow so the agent can say “I’ve started that” and then send a desktop or spoken notification back to the user when the work is finished. This is the first asynchronous workflow to prove the bot can act and then report completion.
6. Add a smoke-test pass for the five capabilities using CLI or a local test harness: list files in a known folder; search for a matching filename; open a folder; open a text file and return readable content; trigger a notification. Validate the agent uses the tool calls in a natural-turn scenario rather than a hard-coded command match.

**Relevant files**
- AssistantToolsFactory.cs — add the first five AIFunction registrations and keep descriptions narrow and precise.
- Program.cs — ensure the Copilot session is constructed with the new tool set and that the app continues to boot in the existing transport pattern.
- A new service such as ComputerToolsService.cs — implement path resolution, file searching, file opening, and notification logic separately from the Copilot runtime.
- README.md — document the new proof-of-concept capability and the safety boundaries for the first build.

**Verification**
1. Run a clean build for the project and confirm the new tool registrations compile with the existing Copilot SDK integration.
2. Execute a CLI smoke test that asks the assistant to: “Show me files beginning with Axxonlab in Downloads.”
3. Execute a second smoke test: “Find the latest Axxonlab PDF in Downloads and open it.”
4. Execute a third smoke test: “Tell me when finished.” and confirm the assistant emits a notification/event after the tool chain completes.
5. Manually check the results are deterministic: no hidden browser automation, no unsupported file actions, and each tool returns a clear success or error object.

**Decisions**
- Scope: only file-system tools and a user notification tool for the first POC.
- Exclude: screen-control, mouse/keyboard automation, browser control, and general application automation.
- Safety model: only allow known folder roots and explicit file operations; reject ambiguous paths.
- Interaction style: prefer natural language commands that resolve into these tools instead of hard-coded voice-command parsing.

**Further Considerations**
1. Decide whether the initial POC should target Downloads and Documents only or include a more general user-profile root search with a visible allowlist.
2. Choose notification channel: Windows toast, console log, or spoken output through the existing TTS layer.
3. Keep the tool response format concise and machine-readable so the Copilot layer can summarize it naturally without over-explaining raw file metadata.
4. If anything involves deleting or editing a file, ensure there is an explicit confirmation step and that the action is logged for audit purposes.