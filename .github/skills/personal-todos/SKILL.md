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
