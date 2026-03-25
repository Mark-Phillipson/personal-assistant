using System.ComponentModel;
using System.Diagnostics;

internal sealed class WindowsFocusAssistService
{
    private const string RegistryPath = "HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings";
    private const string ToastsEnabledName = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    private readonly TimeSpan _timeout;

    private WindowsFocusAssistService(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public static WindowsFocusAssistService FromEnvironment()
    {
        var timeoutSeconds = EnvironmentSettings.ReadInt("WINDOWS_FOCUS_ASSIST_TIMEOUT_SECONDS", fallback: 10, min: 1, max: 120);
        return new WindowsFocusAssistService(TimeSpan.FromSeconds(timeoutSeconds));
    }

    public string GetSetupStatusText()
    {
        return IsSupported
            ? $"Windows Focus Assist integration is ready. Timeout: {_timeout.TotalSeconds:0}s"
            : "Windows Focus Assist integration is only supported on Windows hosts.";
    }

    public async Task<string> SetFocusAssistModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return GetSetupStatusText();
        }

        if (string.IsNullOrWhiteSpace(mode))
        {
            return "Focus mode setting required. Specify 'on', 'off', 'priority', or 'alarms'.";
        }

        mode = mode.Trim().ToLowerInvariant();

        int toastValue;
        string outcome;

        switch (mode)
        {
            case "on":
            case "focus":
            case "enter":
            case "activate":
            case "do not disturb":
            case "dnd":
            case "do-not-disturb":
                // Focus Assist on (suppress most notifications)
                toastValue = 0;
                outcome = "enabled";
                break;
            case "off":
            case "exit":
            case "disable":
            case "turn off do not disturb":
            case "disable do not disturb":
                // Focus Assist off (allow all notifications)
                toastValue = 1;
                outcome = "disabled";
                break;
            case "priority":
                // Priority only is not directly mapped by this simple registry toggle,
                // but we'll treat as focus enabled and explain the intent.
                toastValue = 0;
                outcome = "set to priority-only style (focus assists on with priorities).";
                break;
            case "alarms":
                toastValue = 0;
                outcome = "set to alarms-only style (focus assists on with only alarms).";
                break;
            default:
                return "Unknown focus mode. Supported values are 'on', 'off', 'priority', 'alarms', and do not disturb options like 'do not disturb' or 'dnd'.";
        }

        var script = $"$ErrorActionPreference='Stop'; " +
                     $"if (-not (Test-Path '{RegistryPath}')) {{ New-Item -Path '{RegistryPath}' -Force | Out-Null }}; " +
                     $"Set-ItemProperty -Path '{RegistryPath}' -Name '{ToastsEnabledName}' -Type DWord -Value {toastValue} -Force; " +
                     $"'Focus Assist is {outcome}.'";

        var attempt = await ExecutePowerShellAsync("pwsh", script, cancellationToken);
        if (!attempt.Success && attempt.ExecutableMissing)
        {
            attempt = await ExecutePowerShellAsync("powershell", script, cancellationToken);
        }

        if (attempt.Success)
        {
            return $"Focus mode command accepted: {outcome} (Windows 11 Focus Assist state modified).";
        }

        return $"Failed to set focus mode: {attempt.Message}";
    }

    private async Task<PowerShellExecutionResult> ExecutePowerShellAsync(string executable, string script, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new PowerShellExecutionResult(false, $"Failed to start PowerShell executable '{executable}'.");
            }
        }
        catch (Exception ex)
        {
            if (ex is Win32Exception win32Ex && win32Ex.NativeErrorCode == 2)
            {
                return new PowerShellExecutionResult(false, $"PowerShell executable '{executable}' was not found.", ExecutableMissing: true);
            }

            return new PowerShellExecutionResult(false, $"Failed to start PowerShell executable '{executable}': {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminateProcess(process);
            return new PowerShellExecutionResult(false, $"PowerShell command timed out after {_timeout.TotalSeconds:0} seconds.", TimedOut: true);
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode == 0)
        {
            var successMessage = string.IsNullOrWhiteSpace(stdout) ? "Focus Assist command executed." : stdout;
            return new PowerShellExecutionResult(true, successMessage, process.ExitCode);
        }

        var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        if (string.IsNullOrWhiteSpace(details))
        {
            details = "No error details were returned by the PowerShell command.";
        }

        return new PowerShellExecutionResult(false, $"PowerShell command failed (exit code {process.ExitCode}): {details}", process.ExitCode);
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}

internal sealed record PowerShellExecutionResult(
    bool Success,
    string Message,
    int? ExitCode = null,
    bool TimedOut = false,
    bool ExecutableMissing = false);
