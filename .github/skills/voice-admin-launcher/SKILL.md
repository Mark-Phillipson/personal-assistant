---
name: voice-admin-launcher
description: 'Manage and query Voice Admin launcher actions with read-only, launcher-focused operations. Use this for Voice Admin launcher search and launch flows.'
argument-hint: 'Ask about open launcher searches.'
user-invocable: false
---

# Voice Admin Launcher Skill

Use this skill to handle Voice Admin launcher interactions without embedding workflow details into the system prompt.

## Procedure

1. For launcher searches and launches:
   - search_voice_admin_launchers(keyword)
   - if the user asks to launch, require a valid ID: either provided or determined from the search result.
   - launch_voice_admin_launcher(id)
   - do not guess IDs.

## Behavior

- Always return a concise, actionable natural-language summary of the operation and results.
