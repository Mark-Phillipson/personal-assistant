Yes, Mark. **You can build this into your own .NET voice assistant, and the GitHub Copilot SDK is actually a very good starting point.** You don't need Codex or Claude specifically.

The important distinction is that there are two different kinds of "computer use":

1. **AI controlling the computer through tools** — ideal for what you're describing.
2. **AI looking at the screen and clicking/typing like a human** — useful for arbitrary applications, but considerably more complicated and potentially less reliable.

For your particular use case, I'd start with **tools rather than screen-control**.

### Your example

You say:

> "Go to my Downloads folder and display all files beginning with Axxonlab."

Your voice layer could send that directly to the Copilot agent:

```text
Go to my Downloads folder and display all files beginning with Axxonlab
```

The Copilot agent decides it needs something like:

```text
FindFiles(
    folder: "Downloads",
    pattern: "Axxonlab*"
)
```

Your C# application executes that tool using normal .NET APIs:

```csharp
Directory.GetFiles(
    Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile),
        "Downloads"),
    "Axxonlab*");
```

The results can then be displayed in your UI and spoken back to you.

The **GitHub Copilot SDK explicitly supports custom tools implemented in your application**, including .NET, and the agent can decide when to invoke them. ([GitHub][1])

### And it can go considerably further

You could give your assistant a collection of computer-oriented tools:

```text
FindFiles
OpenFile
MoveFile
CopyFile
DeleteFile
RenameFile
CreateFolder
OpenFolder
SearchFiles
ReadFile
LaunchApplication
CloseApplication
GetActiveWindow
GetScreenInformation
Click
TypeText
PressKey
WaitForApplication
```

Then you could say things like:

> "Find the latest Axxonlab PDF in Downloads and open it."

or:

> "Move all the Axxonlab PDFs into my Axxonlab folder."

or:

> "Open my Downloads folder and show me anything downloaded today."

The LLM doesn't need to know how to perform those operations itself. **It chooses your C# tool and supplies the arguments.**

That's much more controllable than giving an AI unrestricted access to Windows.

---

## The really interesting part: asynchronous tasks

This also fits what you described about:

> "Ask for a task to be completed and then notify me when it's finished."

That's very achievable.

For example:

> **"Find all the Axxonlab PDFs in Downloads, move them into the Axxonlab folder and tell me when you've finished."**

Your assistant could:

```text
Voice input
    ↓
Copilot Agent
    ↓
Plans task
    ↓
C# computer tools
    ↓
Task runs
    ↓
Completed event
    ↓
"Mark, that's finished."
```

You could even have the assistant say:

> "I've started that. I'll let you know when it's finished."

and then your application could produce a Windows notification or spoken notification when the agent reports completion.

The Copilot SDK exposes session/lifecycle events, and custom agents can also emit completion/failure information, which gives you the infrastructure for this sort of UI. ([GitHub][2])

---

# Where "computer use" comes in

There is another level I'd add **after** the basic tools work.

Suppose you say:

> "Open Edge, go to the Sunbury Handcycling Club site and find the latest newsletter."

A specialised browser tool could handle that.

Then:

> "Open the PDF and tell me what the newsletter says about the next ride."

The agent could retrieve and analyse the PDF.

And eventually you could have a general-purpose:

```text
ComputerUse
```

tool capable of interacting with applications that don't have a dedicated API.

That's the bit usually referred to as **computer use**: the model can inspect a screen and perform actions such as clicking, typing and navigating.

But I wouldn't make that the foundation of your system.

### I'd build it in layers

**Layer 1 — Windows tools**

```text
Files
Folders
Applications
Clipboard
Windows notifications
```

**Layer 2 — specialised tools**

```text
Browser
Outlook
Gmail
PDF
Word
Excel
```

**Layer 3 — general computer control**

```text
Screen
Mouse
Keyboard
Window management
```

That gives you much better reliability.

For example, finding `Axxonlab*` files shouldn't require an AI to look at your screen and visually locate them. Windows/.NET can do that **deterministically and instantly**.

---

## And this is where your existing project gets interesting

Because you're already using the **GitHub Copilot SDK with C#/.NET**, I wouldn't start another AI framework.

I'd consider adding a **Computer Tools layer** to your existing assistant:

```text
Your Voice Assistant
        │
        ▼
GitHub Copilot SDK
        │
        ├── File tools
        ├── Windows tools
        ├── Browser tools
        ├── Application tools
        └── Computer-use tool
                 │
                 ▼
              Windows
```

The SDK is designed specifically to allow your application to provide tools to the Copilot agent. ([GitHub][3])

You can also restrict which tools an agent has access to, which is particularly useful for something that can operate your computer. ([GitHub Docs][4])

### I think your first proof-of-concept should be tiny

I'd start with just **five voice commands**, but don't hard-code the commands.

Give the Copilot agent these tools:

```text
ListFiles
SearchFiles
OpenFolder
OpenFile
NotifyUser
```

Then you should be able to say naturally:

> "Show me the files beginning with Axxonlab in Downloads."

> "What's in my Downloads folder?"

> "Find the latest Axxonlab PDF."

> "Open the latest one."

> "Tell me when you've finished."

That would effectively turn your existing assistant into a **voice-controlled Windows agent**, without requiring you to touch the keyboard or mouse.

And importantly, **this isn't something restricted to Codex or Claude**. GitHub's current Copilot SDK is explicitly intended for embedding the Copilot agent runtime into applications and supports .NET and custom tools. ([GitHub][3])

If you want, I can sketch out the **actual C# architecture for adding a `ComputerTools` service to your existing GitHub Copilot SDK project**, including the first `FindFiles` and `NotifyUser` tools.

[1]: https://github.com/github/copilot-sdk/blob/main/docs/getting-started.md?utm_source=chatgpt.com "copilot-sdk/docs/getting-started.md at main · github/copilot-sdk · GitHub"
[2]: https://github.com/github/copilot-sdk/blob/main/docs/features/custom-agents.md?utm_source=chatgpt.com "copilot-sdk/docs/features/custom-agents.md at main · github/copilot-sdk · GitHub"
[3]: https://github.com/github/copilot-sdk/blob/main/README.md?utm_source=chatgpt.com "copilot-sdk/README.md at main · github/copilot-sdk · GitHub"
[4]: https://docs.github.com/en/copilot/how-tos/copilot-sdk/features/custom-agents?utm_source=chatgpt.com "Custom agents and sub-agent orchestration - GitHub Docs"
