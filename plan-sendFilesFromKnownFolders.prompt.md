Plan: Telegram Send Files from Known Folders

## TL;DR
Enable the assistant to list files in known folders (Documents, Desktop, Downloads, Pictures, Videos, repo, repos) and send any file as a Telegram attachment. Requires: adding `SendDocumentAsync` to `TelegramApiClient`, extending `KnownFolderExplorerService` with list/resolve methods, registering a general `list_files_in_folder` tool in `AssistantToolsFactory`, and wiring a per-chat `send_file_to_telegram` tool in `TelegramMessageHandler.GetOrCreateSessionAsync` (so it captures the correct `chatId` and `TelegramApiClient`).

---

## Phase 1 — Extend `KnownFolderExplorerService.cs`

1. Add `repos` alias → reads from env `ASSISTANT_REPOS_DIRECTORY`, defaults to the parent directory of `ASSISTANT_REPO_DIRECTORY` (e.g. `C:\Users\MPhil\source\repos`).
   - Update `TryResolveAlias` to handle "repos" / "source" / "allrepos"

2. Add public `ListFilesAsync(string alias, string? subPath = null, string? fileFilter = null, int maxResults = 50)` method:
   - Validates alias via `TryResolveAlias`
   - Resolves directory using existing `ResolveDirectoryPath` (with traversal guard `IsUnderRoot`)
   - Enumerates files with `Directory.EnumerateFiles(dir, fileFilter ?? "*", SearchOption.TopDirectoryOnly)`
   - Returns formatted string: file name, relative path (relative to the folder root), file size, last modified
   - Truncates to `maxResults`

3. Add public `TryResolveFilePath(string alias, string relativeFilePath, out string fullPath)` method:
   - Validates alias, combines `root + relativeFilePath`, calls `Path.GetFullPath`
   - Guards against traversal with `IsUnderRoot`
   - Checks `File.Exists(fullPath)` and returns true/false + out param

✅ Phase 1 completed: `KnownFolderExplorerService.cs` updated with `repos` alias, `ListFilesAsync`, and `TryResolveFilePath`.

---

## Phase 2 — Add `SendDocumentAsync` to `TelegramApiClient.cs`

4. Add public `SendDocumentAsync(long chatId, string filePath, CancellationToken cancellationToken)`:
   - Guard: throw if `new FileInfo(filePath).Length > 50MB`
   - Build `MultipartFormDataContent`: `chat_id` string field + `document` `StreamContent` with the file's name
   - Use `PostAsyncWithRetry` pattern but with a new `PostMultipartAsyncWithRetry` helper (multipart doesn't use `FormUrlEncodedContent`)
   - Parse JSON response using existing `TelegramApiResponse<JsonElement>` pattern; throw on failure

✅ Phase 2 in progress/completed: Added `SendDocumentAsync` and `PostMultipartAsyncWithRetry` to `TelegramApiClient.cs`.

---

## Phase 3 — Register `list_files_in_folder` in `AssistantToolsFactory.cs`

5. Add a new `AIFunction` to the returned list (after the existing `KnownFolderExplorerService` tools):
   - tool name: list_files_in_folder
   - params: alias (documents/desktop/downloads/pictures/videos/repo/repos), subPath? (optional), fileFilter? (optional, e.g. "*.pdf"), maxResults? (default 50)
   - description: "List files in a known folder (documents, desktop, downloads, pictures, videos, repo, repos) so the user can pick a file to send."

✅ Phase 3 completed: `list_files_in_folder` registered in `AssistantToolsFactory.cs`.

---

## Phase 4 — Wire per-chat `send_file_to_telegram` in `TelegramMessageHandler.cs`

6. Add `KnownFolderExplorerService knownFolderExplorerService` parameter to `HandleAsync`.

7. Modify `GetOrCreateSessionAsync` to accept two extra parameters: `TelegramApiClient telegramClient` and `KnownFolderExplorerService folderService`.

8. Inside `GetOrCreateSessionAsync`, create a per-chat `AIFunction`:
   - tool name: send_file_to_telegram
   - params: folderAlias (string), relativeFilePath (string)
   - description: "Send a file from a known folder to the current Telegram chat. Provide the folder alias (documents/desktop/downloads/pictures/videos/repo/repos) and the relative file path."
   - Implementation: validates path via `folderService.TryResolveFilePath`, calls `telegramClient.SendDocumentAsync(chatId, fullPath, ct)`. Returns success/error string.

9. Combine `assistantTools` with the per-chat tool:
   - `var allTools = assistantTools.Append(sendFileTool).ToList();`
   - use `allTools` in `SessionConfig.Tools`

✅ Phase 4 completed: `TelegramMessageHandler` now includes `send_file_to_telegram` per chat and wiring in `Program.cs`.

---

## Phase 5 — Update `Program.cs`

10. In the `TelegramMessageHandler.HandleAsync(...)` call inside `RunTelegramAsync`, add `knownFolderExplorerService` (already available as a parameter in `RunTelegramAsync`).

---

## Phase 6 — Update `SystemPromptBuilder.cs`

11. Add guidance to the system prompt:
    - Can list files in known folders (`list_files_in_folder`) to help user pick a file
    - Can send any file to Telegram via `send_file_to_telegram` with folder alias + relative path
    - Allowed aliases: documents, desktop, downloads, pictures, videos, repo, repos

---

## Verification
1. Build: `dotnet build -p:UseAppHost=false -p:OutDir=bin\Debug\net10.0\`
2. Manual: Ask bot "list my documents" → should return file listing
3. Manual: Ask bot "send me the file report.pdf from documents" → file arrives in Telegram
4. Manual: Ask bot "list pictures" → returns image files
5. Manual: Try path traversal e.g. "documents" + "../../etc/passwd" → should be blocked
6. Manual: Try sending a file > 50MB → should get a clear error message

---

## Decisions
- No new .cs file needed — all logic fits in existing files
- `send_file_to_telegram` is per-chat (captures chatId + TelegramApiClient in closure); `list_files_in_folder` is general
- Use `sendDocument` for all file types (not `sendPhoto`); Telegram auto-renders images
- `repos` alias added for parent repos folder; existing `repo` alias preserved for current project
- File size limit: 50 MB (Telegram Bot API hard limit for `sendDocument`)
- No file type restrictions per user request (any file type)
