using System.ComponentModel;
using System.Diagnostics;
using System.Text;

internal sealed class ClipboardAssistantService
{
    private const int MaxClipboardTextLength = 100_000;

    private readonly TimeSpan _timeout;

    private ClipboardAssistantService(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public static ClipboardAssistantService FromEnvironment()
    {
        var timeoutSeconds = EnvironmentSettings.ReadInt("CLIPBOARD_TIMEOUT_SECONDS", fallback: 10, min: 1, max: 60);
        return new ClipboardAssistantService(TimeSpan.FromSeconds(timeoutSeconds));
    }

    public string GetSetupStatusText()
    {
        if (!IsSupported)
        {
            return "Clipboard integration is only supported on Windows hosts.";
        }

        return $"Clipboard integration is configured. Timeout: {_timeout.TotalSeconds:0}s";
    }

    public async Task<ClipboardExecutionResult> SetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return new ClipboardExecutionResult(false, GetSetupStatusText());
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ClipboardExecutionResult(false, "No text provided. Share the text you want copied to the clipboard.");
        }

        if (text.Length > MaxClipboardTextLength)
        {
            return new ClipboardExecutionResult(
                false,
                $"Clipboard text is too long. Maximum length is {MaxClipboardTextLength:N0} characters.");
        }

        var script = BuildPowerShellClipboardScript(text);

        var attempt = await ExecutePowerShellAsync("pwsh", script, cancellationToken);
        if (attempt.Success || !attempt.ExecutableMissing)
        {
            return attempt;
        }

        return await ExecutePowerShellAsync("powershell", script, cancellationToken);
    }

    public async Task<string> SetClipboardTextForAssistantAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await SetClipboardTextAsync(text, cancellationToken);
        return result.Message;
    }

    private async Task<ClipboardExecutionResult> ExecutePowerShellAsync(string executable, string script, CancellationToken cancellationToken)
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
                return new ClipboardExecutionResult(false, $"Failed to start '{executable}' to update the clipboard.");
            }
        }
        catch (Exception ex)
        {
            if (ex is Win32Exception win32Ex && win32Ex.NativeErrorCode == 2)
            {
                return new ClipboardExecutionResult(
                    false,
                    $"Clipboard shell '{executable}' was not found.",
                    ExecutableMissing: true);
            }

            return new ClipboardExecutionResult(false, $"Failed to launch clipboard command: {ex.Message}");
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
            return new ClipboardExecutionResult(false, $"Clipboard command timed out after {_timeout.TotalSeconds:0} seconds.", TimedOut: true);
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode == 0)
        {
            var successMessage = string.IsNullOrWhiteSpace(stdout)
                ? "The text has been copied to your clipboard."
                : stdout;

            return new ClipboardExecutionResult(true, successMessage, process.ExitCode);
        }

        var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        if (string.IsNullOrWhiteSpace(details))
        {
            details = "No error details were returned by the clipboard command.";
        }

        return new ClipboardExecutionResult(false, $"Clipboard update failed (exit code {process.ExitCode}): {details}", process.ExitCode);
    }

    private static string BuildPowerShellClipboardScript(string text)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        return string.Join(';',
            "$ErrorActionPreference='Stop'",
            $"$text=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{base64}'))",
            "Set-Clipboard -Value $text",
            "$check=Get-Clipboard -Raw",
            "if($check -eq $text){'The text has been copied to your clipboard.'}else{'Clipboard command executed, but verification did not match exactly.'}");
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

internal sealed record ClipboardExecutionResult(
    bool Success,
    string Message,
    int? ExitCode = null,
    bool TimedOut = false,
    bool ExecutableMissing = false);
