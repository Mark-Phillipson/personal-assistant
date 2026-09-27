# Edge Current Tab PoC

This is the minimum viable proof-of-concept for an Edge extension + native messaging host pattern.

## What it does

- loads an unpacked Edge extension from the `extension` folder
- reads the active tab's visible DOM text and metadata
- sends the payload to a local native messaging host
- the native host receives JSON over stdin/stdout using the Chromium native messaging protocol

## Structure

- `extension/manifest.json` — Edge extension manifest
- `extension/background.js` — active-tab extraction and native-message send
- `nativehost/EdgeTabNativeHost.csproj` — .NET host project
- `nativehost/Program.cs` — native messaging host implementation

## Edge setup

1. Open `edge://extensions` in Microsoft Edge.
2. Turn on Developer mode.
3. Load unpacked and select the `extension` folder.
4. Click the extension icon to trigger the current-tab capture.

## Native Messaging host registration

Create a Windows registry key like this:

```reg
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Microsoft\Edge\NativeMessagingHosts\com.personalassistant.edgetab]
@="C:\\path\\to\\nativehost\\com.personalassistant.edgetab.json"
```

The registry JSON file should look like this:

```json
{
  "name": "com.personalassistant.edgetab",
  "description": "Personal Assistant Edge Tab Host",
  "path": "C:\\path\\to\\nativehost\\EdgeTabNativeHost.exe",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://<extension-id>/"
  ]
}
```

## Notes

This is intentionally minimal and intentionally safe:

- no cookies are read
- no passwords are accessed
- no browser history is accessed
- access is restricted to the active tab DOM only
- the webpage content is treated as untrusted input

## Current status

The native host builds successfully. The remaining step is end-to-end browser validation in Edge.
