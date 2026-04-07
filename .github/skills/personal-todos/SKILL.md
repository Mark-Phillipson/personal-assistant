---
name: personal-todos
description: 'Manage personal todos stored as GitHub Issues in the Personal-Todos private repository. Use for listing, adding, editing titles/descriptions, and completing (closing) personal todos.'
argument-hint: 'Ask to list todos, add a todo, edit an existing one, or mark one as done.'
user-invocable: true
---

# Personal Todos Skill

Use this skill for all personal todo interactions. Todos are stored as GitHub Issues in the `Personal-Todos` private repository.

## Procedure

### Listing todos
1. Call `list_personal_todos(label?, maxResults?, htmlFormat?)`.
2. Return a clear summary with issue numbers, titles, and labels.
3. If a label/project filter is given by the user, pass it as `label`.

### Adding a todo
1. Call `add_personal_todo(title, body?, label?)`.
2. `title` is required; `body` is optional detail/context; `label` is optional project/category.
3. Confirm the created issue number and URL to the user.
4. Do not chain `list_personal_todos` in the same turn unless the user asks.

### Title extraction guidance
When the user provides a single freeform todo request (for example: "Buy printer ink and check the cartridge model, and also order labels for the office"), do not place the entire text into the `title` parameter. Instead:

1. Generate a concise, human-readable `title` that summarizes the action (aim for 40-80 characters; prefer under 60).
2. Put the remaining detail, context, or subtasks into the `body` parameter so the issue description contains the full user intent.
3. If the user message already looks like a short title (one phrase or a short imperative sentence), use it as `title` and leave `body` empty unless they provided additional context.
4. If the user explicitly specifies a `title` and `body`, use their inputs verbatim.

Rationale: GitHub issue titles should be short and searchable while the body holds details and context. Follow this rule when mapping natural language to `add_personal_todo(title, body?, label?)` calls.

### Editing a todo (update title or description)
1. If the user does not provide an issue number, call `list_personal_todos` first to find it.
2. Call `update_personal_todo(issueNumber, newTitle?, newBody?)`.
3. At least one of `newTitle` or `newBody` must be provided.
4. Confirm the update to the user.

### Completing a todo
1. If the user does not provide an issue number, call `list_personal_todos` first to identify the correct one.
2. Call `complete_personal_todo(issueNumber)`.
3. Confirm the todo is now closed.

### Setup check
- If any tools fail or return a not-configured message, call `personal_todos_status` and relay the setup instructions to the user.

## Behavior

- Always confirm the issue number and title after create/update/complete operations.
- Do not guess issue numbers — use `list_personal_todos` to resolve them if the user only provides a title or keyword.
- Personal todos are separate from Voice Admin todos. Do not use Voice Admin todo tools for personal todos.
- For Telegram, default to `htmlFormat=true` to get preformatted table output with `<pre>` blocks.
- Provide the issue URL when adding or updating a todo, so the user can open it directly if needed.
