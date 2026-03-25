## Plan: Generic Database Access

Add a provider-aware database exploration layer so the assistant can inspect Voice Admin SQLite and selected localhost SQL Server databases through one consistent toolset. The recommended implementation is to ship a read-only V1 that supports database discovery, table/schema inspection, row counts, previews, filtered table reads, and optional freeform SELECT-only SQL, while designing the registry and execution layer so a stricter admin mode can be added later without rewriting the feature.

**Steps**
1. Phase 1: inventory and configuration foundation. Add a database registry model that can represent multiple named data sources, including the existing Voice Admin SQLite file and one or more SQL Server targets. This step blocks all later steps.b
2. Define the configuration contract for data sources. Support both configured aliases and live localhost SQL Server discovery, with friendly names for user-facing selection and provider metadata for routing. This should include provider type, connection target, default schema, read-only flag, timeout, and optional allow/deny lists. Depends on step 1.
3. Phase 2: provider abstraction. Introduce a generic database service boundary that separates tool orchestration from provider-specific access. Create one provider for SQLite and one for SQL Server. Reuse the current dynamic schema inspection pattern from the Voice Admin search service for the SQLite implementation. Depends on step 2.
4. Implement shared schema operations across providers: list configured databases, list tables/views, describe table columns with datatypes and nullability, count rows, preview top rows, and resolve object names safely. These are the core user workflows and should be the first executable slice. Depends on step 3.
5. Phase 3: query capabilities. Add guarded query operations for filtered table reads and ad hoc SQL. Recommended delivery split:
6. First deliver a safe query path that supports generated SELECT queries for common prompts like record counts, top rows, field lists, and keyword search across a chosen table. Depends on step 4.
7. Then add explicit freeform SQL in read-only mode, limited to SELECT and metadata queries only, with parser-level or keyword-based rejection of write and administrative statements. This can run in parallel with step 6 once the provider abstraction exists, but should not ship before the schema and timeout safeguards are in place.
8. Phase 4: Copilot tool integration. Register generic tools that let the assistant select a database, inspect schema, and run queries without table-specific code paths. Add prompt guidance telling the model when to use schema tools first, when to ask the user to choose a database, and when to avoid unsafe SQL. Depends on steps 4 and 6; step 7 extends the toolset.
9. Keep existing Voice Admin tools intact and layer the generic database tools beside them. This avoids regressions in the launcher/todo workflows while allowing the same Voice Admin database to be addressed through the new generic path when useful. Parallel with step 8 once the provider layer is stable.
10. Phase 5: verification and hardening. Add targeted tests for connection routing, schema inspection, identifier quoting, statement rejection, result limiting, timeout handling, and SQL Server discovery on localhost. Verify the assistant prompt behavior manually in CLI mode for tasks like “how many rows are in X”, “what columns does Y have”, and “show the first 10 rows from Z”. Depends on steps 6 through 9.

**Relevant files**
- `c:\Users\MPhil\source\repos\personal-assistant\Program.cs` — instantiate the new registry/service objects from environment configuration and pass them into tool registration.
- `c:\Users\MPhil\source\repos\personal-assistant\AssistantToolsFactory.cs` — add provider-agnostic database tools such as list databases, select database, list tables, describe table schema, count rows, preview rows, and run query.
- `c:\Users\MPhil\source\repos\personal-assistant\SystemPromptBuilder.cs` — add explicit prompt rules for database selection, schema-first behavior, and safe query usage.
- `c:\Users\MPhil\source\repos\personal-assistant\EnvironmentSettings.cs` — extend parsing support if needed for structured multi-database settings, booleans, lists, or JSON config paths.
- `c:\Users\MPhil\source\repos\personal-assistant\personal-assistant.csproj` — add SQL Server client package support.
- `c:\Users\MPhil\source\repos\personal-assistant\README.md` — document the new database tooling, SQL Server localhost setup, configuration examples, and safety constraints.
- `c:\Users\MPhil\source\repos\personal-assistant\VoiceAdminSearchService.cs` — reuse and potentially extract the existing generic SQLite schema inspection and dynamic table search logic.
- `c:\Users\MPhil\source\repos\personal-assistant\VoiceAdminService.cs` — keep current Voice Admin task/launcher workflows separate from the new generic data inspection path.
- `c:\Users\MPhil\source\repos\personal-assistant\GenericDatabaseService.cs` — new orchestration layer for provider-neutral operations and tool-facing responses.
- `c:\Users\MPhil\source\repos\personal-assistant\DatabaseRegistry.cs` — new registry for configured aliases and provider resolution.
- `c:\Users\MPhil\source\repos\personal-assistant\IDatabaseProvider.cs` — new provider interface for schema discovery and query execution.
- `c:\Users\MPhil\source\repos\personal-assistant\SqliteDatabaseProvider.cs` — new SQLite implementation for Voice Admin and any future SQLite files.
- `c:\Users\MPhil\source\repos\personal-assistant\SqlServerDatabaseProvider.cs` — new SQL Server implementation for localhost discovery and selected database queries.

**Status**
- Phase 1 complete: database registry and config foundation implemented (voice admin legacy fallback + DATABASE_CONFIG_PATH / DATABASE_CONFIG_JSON support).
- Phase 2 complete: provider abstraction + generic schema tools implemented, including safe object resolution and read-only listing/query operations.
- Phase 3 complete: query capability tools added (query_table_rows, execute_read_only_sql), plus SQL read-only guard and phase-three tests (custom runner + xUnit project) added.
- Phase 4 in progress: Copilot tool integration added (list_databases, select_database, list_tables, describe_table_schema, count_table_rows, preview_table_rows, resolve_table_object, query_table_rows, execute_read_only_sql) and SystemPromptBuilder guidance updated.
- `c:\Users\MPhil\source\repos\personal-assistant\DatabaseModels.cs` — new shared result models for database, table, column, and query metadata.

**Verification**
1. Add unit tests for registry parsing and alias resolution, including the Voice Admin alias pointing to the existing SQLite file.
2. Add provider-level tests for SQLite schema discovery using the Voice Admin database shape and for SQL Server schema discovery against a local test database or mocked metadata queries.
3. Add tests that reject unsafe statements for freeform SQL, including INSERT, UPDATE, DELETE, ALTER, DROP, EXEC, USE, and multi-statement batches.
4. Add tests for identifier quoting and object resolution so tables with spaces or mixed casing are handled correctly.
5. Run the repo verify build using the non-apphost pattern already documented for this codebase.
6. Run CLI smoke tests for prompts like: list available databases, show tables in Voice Admin, what fields does Todos have, how many rows are in Launcher, preview 10 rows from Transactions, and run a SELECT against a chosen SQL Server database.
7. Manually verify that the model asks the user to choose a database when the prompt is ambiguous and that it does not claim results without calling the relevant tool first.

**Decisions**
- Included: multi-database support across Voice Admin SQLite and localhost SQL Server, with user-selectable databases and generic schema/data inspection tools.
- Included: both configured aliases and live localhost SQL Server database discovery.
- Requested by discussion: broad ad hoc SQL is a desired end state.
- Recommended boundary: V1 should execute only read-only SQL even if the architecture allows a future admin mode. This matches the stated workflow need of inspecting data without opening SSMS and avoids turning the assistant into a database write surface immediately.
- Excluded from V1: non-read-only SQL execution, stored procedures, cross-server traversal, SQL Agent interactions, and remote SQL Server access. Remote support should be planned in the architecture but deferred from the first implementation.
- Existing Voice Admin-specific tools remain first-class and should not be replaced by the generic path.

**Further Considerations**
1. Configuration format recommendation: prefer a single JSON config file path in environment over dozens of flat env vars, because alias metadata, provider type, default schema, and allowlists will become unwieldy in plain env keys.
2. SQL Server discovery recommendation: enumerate only non-system databases by default and expose system databases only behind an explicit configuration flag.
3. Freeform SQL recommendation: require the user to specify the target database alias every time rather than relying on conversational session state for destructive ambiguity reduction.
