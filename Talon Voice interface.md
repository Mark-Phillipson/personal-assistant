# Talon Voice Interface for Bob CLI

This document provides copy/paste-ready Talon and Python scripts to send spoken commands to personal-assistant using CLI mode:

`--cli "<prompt>"`

## 1) Python Action File

Create this file in your Talon user repo:

`C:\Users\MPhil\AppData\Roaming\talon\user\mystuff\talon_my_stuff\bob_cli.py`

```python
from talon import Module, actions
import subprocess

mod = Module()

# Use the verify DLL path so this works even when apphost is locked.
ASSISTANT_COMMAND = [
    "dotnet",
    r"C:\Users\MPhil\source\repos\personal-assistant\bin\Debug\net10.0-verify\personal-assistant.dll",
]


@mod.action_class
class Actions:
    def bob_cli_send(command: str):
        """Send free-form dictation to personal-assistant CLI mode."""
        prompt = (command or "").strip()
        if not prompt:
            actions.app.notify("Bob CLI", "No command text captured.")
            return

        try:
            result = subprocess.run(
                ASSISTANT_COMMAND + ["--cli", prompt],
                capture_output=True,
                text=True,
                encoding="utf-8",
                timeout=120,
            )
        except FileNotFoundError:
            actions.app.notify("Bob CLI", "dotnet command not found.")
            return
        except subprocess.TimeoutExpired:
            actions.app.notify("Bob CLI", "Assistant timed out.")
            return
        except Exception as ex:
            actions.app.notify("Bob CLI", f"Failed to run assistant: {ex}")
            return

        if result.returncode == 0:
            output = (result.stdout or "").strip() or "Done."
            actions.app.notify("Bob", output)
            return

        error_text = (result.stderr or result.stdout or "Unknown error").strip()
        actions.app.notify("Bob CLI Error", error_text)
```

## 2) Talon Command File

Create this file in your Talon user repo:

`C:\Users\MPhil\AppData\Roaming\talon\user\mystuff\talon_my_stuff\bob_cli.talon`

```talon
-
bob <user.text>:
    user.bob_cli_send(text)
```

Example spoken command:

- bob please play Ukraine the latest podcast

## 3) Optional Alternative Phrase

If you prefer a more explicit wake phrase, use this Talon rule instead:

```talon
-
bob please <user.text>:
    user.bob_cli_send(text)
```

## 4) Notes

- This flow sends one prompt per voice command and exits.
- The Python script uses the verify DLL path to avoid apphost lock issues.
- If you move the assistant repo, update `ASSISTANT_COMMAND` path in `bob_cli.py`.
- If responses are long, Talon notifications may truncate text; that is expected.

