
# GitHub Copilot Instructions for Personal Assistant (.NET 10, Copilot SDK, Telegram, Gmail, Calendar)

This file provides project-specific guidelines for using GitHub Copilot in this repository.

## Project Context
- .NET 10 Telegram-based personal assistant using GitHub Copilot SDK
- Integrates with Gmail and Google Calendar via OAuth
- Handles environment variables for secrets and configuration

## Copilot Usage Guidelines
- Use Copilot suggestions as a starting point, not as final code.
- Always review and test Copilot-generated code before merging.
- Ensure Copilot code follows .NET/C# best practices and this project's conventions.
- Avoid committing any sensitive data, tokens, or OAuth credentials suggested by Copilot.
- Document Copilot-generated logic, especially for:
	- Telegram bot message handling
	- Gmail/Calendar API integration
	- AI tool wiring and Copilot SDK usage
- When adding new features, update the README and document any new environment variables or setup steps.

## Recommended Prompts for This Project
- "Write a unit test for Telegram message handler"
- "Refactor Gmail integration for clarity"
- "Suggest a more efficient polling algorithm for Telegram updates"
- "Generate documentation for the Copilot session management logic"
- "Add error handling for failed Gmail API calls"
- "Show how to securely load environment variables in .NET"
- "Write a helper to summarize unread Gmail messages"

## Security and Secrets
- Never commit real tokens, OAuth credentials, or personal data.
- Use environment variables for all secrets (see README for required variables).
- If Copilot suggests code that hardcodes secrets, replace with secure loading from environment.

## Troubleshooting Copilot
- If Copilot suggestions are irrelevant, rephrase your prompt or provide more context (e.g., mention .NET 10, Telegram, Gmail, etc.).
- For Copilot issues, see the [GitHub Copilot documentation](https://docs.github.com/en/copilot).

---
*This file is located at `.github/copilot-instructions.md` and should be updated as project needs evolve.*
