## Plan: Voice Admin Read-Only Launcher Search

Add a dedicated launcher-search AI tool in the personal assistant that queries your Voice Admin SQLite database in read-only mode, matching Name, CommandLine, and CategoryName for prompts like “list launcher records with Upwork.”
This keeps scope tight: query only, no writes, no slash commands.

**Steps**
1. Phase 1: Configuration and startup wiring
2. Add launcher DB environment settings and safe defaults in EnvironmentSettings.cs.
3. Initialize a launcher query service in startup and include status logging in Program.cs.
4. Phase 2: Read-only data service
5. Create a new launcher query service in the project root that uses SQLite read-only connections and parameterized SQL.
6. Implement keyword search across Launcher.Name, Launcher.CommandLine, and Category.CategoryName via LEFT JOINs, with deterministic ordering and de-duplication.
7. Return compact result objects (id, name, command line, category list, optional arguments) and enforce result caps.
8. Add resilient error handling for missing DB file, empty query, lock/read failures, and no-match scenarios.
9. Phase 3: AI tool exposure
10. Register a new tool in AssistantToolsFactory.cs with explicit description for launcher lookup intents.
11. Add prompt guidance in SystemPromptBuilder.cs so the assistant calls the launcher tool for launcher-record search/list requests.
12. Wire tool/service consistently for both CLI and Telegram paths in Program.cs.
13. Phase 4: Docs and validation
14. Document new launcher env vars and usage examples in README.md.
15. Add launcher env var template entries in .env.example.
16. Add SQLite client package dependency in personal-assistant.csproj if not already present.
17. Build and run verification checks.

**Relevant files**
- AssistantToolsFactory.cs
- Program.cs
- SystemPromptBuilder.cs
- EnvironmentSettings.cs
- README.md
- .env.example
- personal-assistant.csproj

**Verification**
1. Set DB path env var to your database location and build the project.
2. If apphost is locked, run: dotnet build -p:UseAppHost=false -p:OutDir=bin\\Debug\\net10.0-verify\\
3. Run in CLI mode and test prompts (use `--cli "<prompt>"`):
4. list launcher records with Upwork
5. find launcher entries containing Blazor
6. Confirm output includes launcher names and category names, and no write behavior exists.
7. Negative checks: missing DB path/file, empty keyword, and no results all return clear user-safe responses.

**Decisions Captured**
- Direct SQLite integration from personal-assistant.
- Read-only access only.
- Search scope: Name, CommandLine, CategoryName.
- AI tool only (no slash commands).
