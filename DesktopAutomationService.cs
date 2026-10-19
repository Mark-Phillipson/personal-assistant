using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class DesktopAutomationService
{
    private const int SwRestore = 9;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfWheel = 0x0800;

    public string GetSetupStatusText()
    {
        return OperatingSystem.IsWindows()
            ? "Desktop automation is available on this Windows host."
            : "Desktop automation is only supported on Windows hosts.";
    }

    public Task<string> GetActiveWindowAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GetSetupStatusText());
        }

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return Task.FromResult("No active window could be determined.");
        }

        return Task.FromResult(DescribeWindow(hwnd, includeHandle: true));
    }

    public Task<string> ListWindowsAsync(int maxResults = 20)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GetSetupStatusText());
        }

        var windows = EnumerateVisibleWindows()
            .Take(Math.Clamp(maxResults, 1, 100))
            .ToList();

        if (windows.Count == 0)
        {
            return Task.FromResult("No visible windows were found.");
        }

        var builder = new StringBuilder();
        for (var i = 0; i < windows.Count; i++)
        {
            builder.AppendLine($"{i + 1}. {windows[i]}");
        }

        return Task.FromResult("Visible windows:\n" + builder);
    }

    public Task<string> FocusWindowAsync(string windowQuery)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GetSetupStatusText());
        }

        if (string.IsNullOrWhiteSpace(windowQuery))
        {
            return Task.FromResult("No window title or process query was provided.");
        }

        var matches = FindWindows(windowQuery).ToList();
        if (matches.Count == 0)
        {
            return Task.FromResult($"No visible window matched '{windowQuery}'. Use list_windows first to inspect titles.");
        }

        var target = matches[0];
        try
        {
            ShowWindow(target.Handle, SwRestore);
            var result = SetForegroundWindow(target.Handle);
            return Task.FromResult(result
                ? $"Focused window: {target}"
                : $"Attempted to focus window: {target} (Windows may still block foreground focus in some cases).");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to focus window '{target.Title}': {ex.Message}");
        }
    }

    public Task<string> ClickAsync(int x, int y, string button = "left", int clickCount = 1)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GetSetupStatusText());
        }

        if (clickCount < 1)
        {
            return Task.FromResult("Click count must be at least 1.");
        }

        var buttonKind = ParseButton(button, out var down, out var up, out var buttonLabel);
        if (!buttonKind)
        {
            return Task.FromResult("Unsupported mouse button. Use left, right, or middle.");
        }

        try
        {
            SetCursorPos(x, y);
            for (var i = 0; i < clickCount; i++)
            {
                SendMouseInput(down, x, y);
                SendMouseInput(up, x, y);
            }

            return Task.FromResult($"Clicked {buttonLabel} button at ({x}, {y}) {clickCount} time(s).");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to click at ({x}, {y}): {ex.Message}");
        }
    }

    public Task<string> ScrollAsync(int delta)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GetSetupStatusText());
        }

        try
        {
            SendMouseWheel(delta);
            return Task.FromResult($"Scrolled the mouse wheel by {delta}.");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to scroll: {ex.Message}");
        }
    }

    public Task<string> TypeTextAsync(string text)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GetSetupStatusText());
        }

        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult("No text was provided to type.");
        }

        try
        {
            foreach (var ch in text)
            {
                SendUnicodeChar(ch);
            }

            return Task.FromResult($"Typed {text.Length} character(s) into the active window.");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to type text: {ex.Message}");
        }
    }

    public Task<string> PressKeysAsync(string keySequence)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GetSetupStatusText());
        }

        if (string.IsNullOrWhiteSpace(keySequence))
        {
            return Task.FromResult("No key sequence was provided.");
        }

        try
        {
            var steps = keySequence.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var step in steps)
            {
                PressSingleSequence(step);
            }

            return Task.FromResult($"Pressed key sequence: {keySequence}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to press key sequence '{keySequence}': {ex.Message}");
        }
    }

    public Task<string> MoveMouseAsync(int x, int y)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GetSetupStatusText());
        }

        try
        {
            SetCursorPos(x, y);
            return Task.FromResult($"Moved mouse to ({x}, {y}).");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to move mouse to ({x}, {y}): {ex.Message}");
        }
    }

    private static IEnumerable<DesktopWindowInfo> EnumerateVisibleWindows()
    {
        var results = new List<DesktopWindowInfo>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            results.Add(CreateWindowInfo(hwnd, title));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static IEnumerable<DesktopWindowInfo> FindWindows(string query)
    {
        var normalized = query.Trim();
        return EnumerateVisibleWindows()
            .Where(window =>
                window.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                window.ProcessName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(window => window.Title, StringComparer.OrdinalIgnoreCase);
    }

    private static DesktopWindowInfo CreateWindowInfo(IntPtr hwnd, string title)
    {
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        var processName = GetProcessName(processId);
        return new DesktopWindowInfo(hwnd, processId, processName, title);
    }

    private static string DescribeWindow(IntPtr hwnd, bool includeHandle)
    {
        var title = GetWindowTitle(hwnd);
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        var processName = GetProcessName(processId);

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "(untitled window)";
        }

        var handlePart = includeHandle ? $", handle=0x{hwnd.ToInt64():X}" : string.Empty;
        return $"{title} | process={processName} (pid {processId}{handlePart})";
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0)
        {
            var buffer = new StringBuilder(256);
            _ = GetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        var sb = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            if (processId == 0)
            {
                return "unknown";
            }

            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool ParseButton(string button, out uint downFlag, out uint upFlag, out string label)
    {
        switch (button.Trim().ToLowerInvariant())
        {
            case "left":
                downFlag = MouseeventfLeftdown;
                upFlag = MouseeventfLeftup;
                label = "left";
                return true;
            case "right":
                downFlag = MouseeventfRightdown;
                upFlag = MouseeventfRightup;
                label = "right";
                return true;
            case "middle":
                downFlag = MouseeventfMiddledown;
                upFlag = MouseeventfMiddleup;
                label = "middle";
                return true;
            default:
                downFlag = 0;
                upFlag = 0;
                label = string.Empty;
                return false;
        }
    }

    private static void SendMouseInput(uint mouseData, int x, int y)
    {
        var inputs = new[]
        {
            new INPUT
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MOUSEINPUT
                    {
                        Dx = x,
                        Dy = y,
                        MouseData = 0,
                        DwFlags = mouseData,
                        Time = 0,
                        DwExtraInfo = IntPtr.Zero
                    }
                }
            }
        };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == 0)
        {
            throw new InvalidOperationException("SendInput failed.");
        }
    }

    private static void SendMouseWheel(int delta)
    {
        var inputs = new[]
        {
            new INPUT
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MOUSEINPUT
                    {
                        Dx = 0,
                        Dy = 0,
                        MouseData = unchecked((uint)delta),
                        DwFlags = MouseeventfWheel,
                        Time = 0,
                        DwExtraInfo = IntPtr.Zero
                    }
                }
            }
        };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == 0)
        {
            throw new InvalidOperationException("SendInput failed.");
        }
    }

    private static void SendUnicodeChar(char ch)
    {
        var inputs = new[]
        {
            new INPUT
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KEYBDINPUT
                    {
                        WVk = 0,
                        WScan = ch,
                        DwFlags = KeyeventfUnicode,
                        Time = 0,
                        DwExtraInfo = IntPtr.Zero
                    }
                }
            },
            new INPUT
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KEYBDINPUT
                    {
                        WVk = 0,
                        WScan = ch,
                        DwFlags = KeyeventfUnicode | KeyeventfKeyup,
                        Time = 0,
                        DwExtraInfo = IntPtr.Zero
                    }
                }
            }
        };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == 0)
        {
            throw new InvalidOperationException("SendInput failed.");
        }
    }

    private static void PressSingleSequence(string sequence)
    {
        var tokens = sequence.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return;
        }

        var modifiers = new List<ushort>();
        ushort? mainKey = null;

        foreach (var token in tokens)
        {
            var vk = ResolveVirtualKey(token, out var isModifier);
            if (vk == 0)
            {
                throw new InvalidOperationException($"Unsupported key token '{token}'.");
            }

            if (isModifier)
            {
                modifiers.Add(vk);
            }
            else if (mainKey is null)
            {
                mainKey = vk;
            }
            else
            {
                throw new InvalidOperationException("Only one non-modifier key is allowed per sequence.");
            }
        }

        var inputs = new List<INPUT>(modifiers.Count * 2 + 2);

        foreach (var modifier in modifiers)
        {
            inputs.Add(CreateKeyInput(modifier, keyUp: false));
        }

        if (mainKey is not null)
        {
            inputs.Add(CreateKeyInput(mainKey.Value, keyUp: false));
            inputs.Add(CreateKeyInput(mainKey.Value, keyUp: true));
        }

        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            inputs.Add(CreateKeyInput(modifiers[i], keyUp: true));
        }

        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) == 0)
        {
            throw new InvalidOperationException("SendInput failed.");
        }
    }

    private static INPUT CreateKeyInput(ushort virtualKey, bool keyUp)
    {
        return new INPUT
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    WVk = virtualKey,
                    WScan = 0,
                    DwFlags = keyUp ? KeyeventfKeyup : 0,
                    Time = 0,
                    DwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static ushort ResolveVirtualKey(string token, out bool isModifier)
    {
        isModifier = false;
        var normalized = token.Trim().ToUpperInvariant();
        return normalized switch
        {
            "CTRL" or "CONTROL" => MarkModifier(0x11, ref isModifier),
            "ALT" or "MENU" => MarkModifier(0x12, ref isModifier),
            "SHIFT" => MarkModifier(0x10, ref isModifier),
            "WIN" or "WINDOWS" or "META" => MarkModifier(0x5B, ref isModifier),
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ when normalized.Length == 1 => (ushort)char.ToUpperInvariant(normalized[0]),
            _ => 0
        };
    }

    private static ushort MarkModifier(ushort vk, ref bool isModifier)
    {
        isModifier = true;
        return vk;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    private sealed record DesktopWindowInfo(IntPtr Handle, uint ProcessId, string ProcessName, string Title)
    {
        public override string ToString() => $"{Title} | {ProcessName} (pid {ProcessId}, handle=0x{Handle.ToInt64():X})";
    }
}
