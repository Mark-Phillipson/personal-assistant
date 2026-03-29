---
name: generic-database-access
description: 'Inspect local databases and run read-only query tools using provider-aware database connectors. Use this skill for database schema discovery, row counts, previews, and SELECT-only queries.'
argument-hint: 'Ask your database question or provide the table name/query details.'
user-invocable: true
---

# Generic Database Access

Use this skill for asking database questions that involve one of the configured database sources (Voice Admin SQLite, localhost SQL Server, or other configured provider aliases).

## Procedure

1. Determine the user’s target database alias now. If alias is missing, invoke list_databases or select_database.
2. For schema inquiry:
   - To list tables: list_tables(alias)
   - To describe columns: describe_table_schema(alias, table)
3. For counts or previews:
   - count_table_rows(alias, table)
   - preview_table_rows(alias, table, rowLimit)
4. For table resolution:
   - resolve_table_object(alias, candidateName)
   - follow with list_tables/describe_table_schema as required.
5. For filters:
   - query_table_rows(alias, table, whereClause, limit)
6. For advanced user-provided queries:
   - execute_read_only_sql(alias, sql)
   - enforce SELECT-only (no INSERT/UPDATE/DELETE/DDL) and validate before execution.
7. Always prefer tool calls over inferred data. Do not guess table contents from memory.
8. Do not execute any write operations, DDL, or update statements within this skill.

## Behavior

- When user asks for exports or CSV output, use export_table_rows_to_csv with openInVsCode=true and return the generated file path.
- For Voice Admin-specific workload, keep the voice-admin-specific toolset separate from generic database access; only use generic tools for open database inspection tasks.
- Use a concise user-facing summary after gathering data (e.g., column list, row count, query results), and include next action suggestions.
