# Talon Voice Interface for Bob

Talon sends voice commands to the already-running Telegram-hosted Bob process over its local HTTP API. This means Talon and Telegram use the same in-memory Copilot conversation and requests are serialized by Bob.

## 1) Python Action File

Create or replace this file in your Talon user repo:

`C:\\Users\\MPhil\\AppData\\Roaming\\talon\\user\\mystuff\\talon_my_stuff\\bob_cli.py`

```python
from talon import Module, actions
import json
import threading
import urllib.error
import urllib.request

mod = Module()

BOB_TALON_API_URL = "http://localhost:5077/api/talon/command"


def _send_bob_command_async(prompt: str):
    try:
        payload = json.dumps({
            "command": prompt,
            "deviceToken": "",
            "deviceName": "Talon",
        }).encode("utf-8")

        request = urllib.request.Request(
            BOB_TALON_API_URL,
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )

        with urllib.request.urlopen(request, timeout=135) as response:
            result = json.loads(response.read().decode("utf-8"))

        if result.get("success"):
            output = (result.get("textResponse") or "").strip() or "Done."
            actions.app.notify("Bob", output)
        else:
            actions.app.notify(
                "Bob Error",
                result.get("textResponse") or "The assistant request failed.",
            )

    except urllib.error.HTTPError as ex:
        try:
            detail = ex.read().decode("utf-8", errors="replace")
        except Exception:
            detail = str(ex)
        actions.app.notify("Bob API Error", detail or str(ex))
    except urllib.error.URLError as ex:
        actions.app.notify(
            "Bob API Unavailable",
            "Make sure Bob is running in Telegram mode. " + str(ex.reason),
        )
    except Exception as ex:
        actions.app.notify("Bob Error", str(ex))


@mod.action_class
class Actions:
    def bob_cli_send(command: str):
        """Send free-form dictation to the running Bob conversation."""
        prompt = (command or "").strip()
        if not prompt:
            actions.app.notify("Bob", "No command text captured.")
            return

        threading.Thread(
            target=_send_bob_command_async,
            args=(prompt,),
            daemon=True,
        ).start()
```

The Bob process speaks the response through its Windows TTS service. Talon only displays the notification, so the same response is not spoken a second time by the Talon action.

## 2) Talon Command File

Create or keep this file in your Talon user repo:

`C:\\Users\\MPhil\\AppData\\Roaming\\talon\\user\\mystuff\\talon_my_stuff\\bob_cli.talon`

```talon
-
bob <user.text>:
    user.bob_cli_send(text)
```

Example spoken command:

- bob please play Ukraine the latest podcast

## 3) Requirements and behaviour

- Start Bob normally in Telegram mode and leave it running.
- The default local API address is `http://localhost:5077/`; change `BOB_TALON_API_URL` if you configured a different `ANDROID_COMPANION_API_PREFIX`.
- Talon uses the dedicated `POST /api/talon/command` route. It bypasses Android phone-command heuristics and sends the dictated text directly to Copilot.
- The Talon route accepts loopback requests only when an Android device token is configured. It does not expose that token to the Talon script.
- Talon requests are not echoed to Telegram. Telegram messages continue to receive their normal Telegram replies.
- Bob speaks Talon responses locally once; Talon displays the returned text as a notification.
- If Bob is not running, Talon reports that the local API is unavailable.
- This keeps the conversation alive in the Telegram-hosted process instead of starting a new .NET process for every voice command.
