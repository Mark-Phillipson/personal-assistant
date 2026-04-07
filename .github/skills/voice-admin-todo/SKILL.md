---
name: voice-admin-todo
description: 'Manage and query Voice Admin launcher actions with read-only and launcher-focused actions. For personal todo management, prefer the personal-todos skill which uses GitHub Issues. This skill is focused on Voice Admin-specific interactions and read-only queries.'
argument-hint: 'Ask about open launcher searches.'
user-invocable: false
---

# Voice Admin Todo + Launcher Skill

Use this skill to handle Voice Admin todo and launcher interactions without embedding workflow details into the system prompt.

## Procedure

1. For launcher searches and launches:
   - search_voice_admin_launchers(keyword)
   - if the user asks to launch, require a valid ID: either provided or determined from the search result.
   - launch_voice_admin_launcher(id)
   - do not guess IDs.

2. For listing open/incomplete todos:
   - list_voice_admin_open_todos(projectOrCategory?)
   - if the user mentions a category or project, use projectOrCategory.

3. For CSV or export flow:
   - export_voice_admin_open_todos_to_csv(projectOrCategory?, maxResults?)
   - if user wants both summary and CSV, output summary + CSV file path.

4. For adding and completing todos: (DEPRECATED)
   - Write operations such as `add_voice_admin_todo`, `complete_voice_admin_todo`, and related assignment APIs are deprecated for personal todo workflows.
   - For personal todo creation, editing, or completion, prefer the `personal-todos` skill which stores todos as GitHub Issues.
   - If a caller attempts to invoke write operations, return a brief guidance message directing users to configure Personal-Todos or use `add_personal_todo`.

6. For assigning/changing category or project:
   - if user provides Todo ID, call assign_voice_admin_todo_project(todoId, projectOrCategory).
   - if no ID, call assign_voice_admin_todo_project_by_text(text, projectOrCategory).

7. Unsafe tool handling:
   - if todo tools fail, tell user explicitly and include error text; do not fabricate counts or success.
   - do not claim zero open tasks unless list_voice_admin_open_todos ran and returned zero.

## Behavior

- Always return a concise, actionable natural-language summary of the operation and results.
- If the user gives vague instructions (e.g., "finish the todo"), ask a follow-up question to clarify which task or ID is intended.
- For posts that mention existing tasks while adding, suggest deduplication checks (do not create duplicates blindly).  
- For user instructions like "what's left" or "todo list", treat them as `list_voice_admin_open_todos` intents.
