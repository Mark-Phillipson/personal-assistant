using System.Diagnostics;
using System.ComponentModel;

internal sealed class NaturalCommandsAssistantService
{
    private const int MaxCommandLength = 500;

    private readonly string _executable;
    private readonly string? _workingDirectory;
    private readonly TimeSpan _timeout;

    private NaturalCommandsAssistantService(string executable, string? workingDirectory, TimeSpan timeout)
    {
        _executable = executable;
        _workingDirectory = workingDirectory;
        _timeout = timeout;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_executable);

    public static NaturalCommandsAssistantService FromEnvironment()
    {
        var executable = Environment.GetEnvironmentVariable("NATURAL_COMMANDS_EXECUTABLE");
        var workingDirectory = Environment.GetEnvironmentVariable("NATURAL_COMMANDS_WORKING_DIRECTORY");
        var timeoutSeconds = EnvironmentSettings.ReadInt("NATURAL_COMMANDS_TIMEOUT_SECONDS", fallback: 15, min: 1, max: 120);

        executable = ResolveExecutable(executable);

        if (!string.IsNullOrWhiteSpace(executable) && (executable.Contains('\\') || executable.Contains('/')))
        {
            executable = Path.GetFullPath(executable);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = Path.GetFullPath(workingDirectory);
        }

        return new NaturalCommandsAssistantService(
            executable,
            string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            TimeSpan.FromSeconds(timeoutSeconds));
    }

    public string GetSetupStatusText()
    {
        if (!IsConfigured)
        {
            return "NaturalCommands integration is not configured. Set NATURAL_COMMANDS_EXECUTABLE, then restart the app.";
        }

        var executableState = LooksLikeFilePath(_executable) && !File.Exists(_executable)
            ? $"(warning: executable path not found at '{_executable}')"
            : "";

        var workingDirectory = _workingDirectory ?? "(current process directory)";
        return $"NaturalCommands integration is configured.\nExecutable: {_executable} {executableState}\nWorking directory: {workingDirectory}\nTimeout: {_timeout.TotalSeconds:0}s";
    }

    public async Task<NaturalCommandExecutionResult> ExecuteAsync(string commandText, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new NaturalCommandExecutionResult(false, GetSetupStatusText());
        }

        if (string.IsNullOrWhiteSpace(commandText))
        {
            return new NaturalCommandExecutionResult(false, "Usage: /natural <command>. Example: /natural show desktop");
        }

        if (commandText.Length > MaxCommandLength)
        {
            return new NaturalCommandExecutionResult(false, $"Command is too long. Maximum length is {MaxCommandLength} characters.");
        }

        commandText = NormalizeLeadingNatural(commandText);

        if (string.IsNullOrWhiteSpace(commandText))
        {
            return new NaturalCommandExecutionResult(false, "No command arguments were provided.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory ?? Environment.CurrentDirectory
        };

        // NaturalCommands contract: NaturalCommands.exe <mode> <dictation>
        startInfo.ArgumentList.Add("natural");
        startInfo.ArgumentList.Add(commandText);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new NaturalCommandExecutionResult(false, "NaturalCommands process did not start.");
            }
        }
        catch (Exception ex)
        {
            if (ex is Win32Exception win32Ex && win32Ex.NativeErrorCode == 2)
            {
                return new NaturalCommandExecutionResult(
                    false,
                    $"NaturalCommands executable was not found. Current setting: '{_executable}'. Set NATURAL_COMMANDS_EXECUTABLE to the full path of NaturalCommands.exe.");
            }

            return new NaturalCommandExecutionResult(false, $"Failed to start NaturalCommands process: {ex.Message}");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminateProcess(process);
            return new NaturalCommandExecutionResult(
                false,
                $"Command timed out after {_timeout.TotalSeconds:0} seconds.",
                TimedOut: true);
        }

        var standardOutput = (await standardOutputTask).Trim();
        var standardError = (await standardErrorTask).Trim();

        if (process.ExitCode == 0)
        {
            var successMessage = string.IsNullOrWhiteSpace(standardOutput)
                ? "Command executed successfully."
                : standardOutput;

            return new NaturalCommandExecutionResult(true, successMessage, process.ExitCode);
        }

        var failureDetails = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        if (string.IsNullOrWhiteSpace(failureDetails))
        {
            failureDetails = "No error details were returned by NaturalCommands.";
        }

        return new NaturalCommandExecutionResult(false, $"Command failed (exit code {process.ExitCode}): {failureDetails}", process.ExitCode);
    }

    public async Task<string> ExecuteForAssistantAsync(string commandText, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(commandText, cancellationToken);
        return result.Message;
    }

    private static bool LooksLikeFilePath(string candidate)
    {
        return candidate.Contains('\\') || candidate.Contains('/') || candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExecutable(string? configuredExecutable)
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            var trimmed = configuredExecutable.Trim();
            if (LooksLikeFilePath(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            return trimmed;
        }

        var discovered = DiscoverLocalExecutable();
        return discovered ?? "natural";
    }

    private static string? DiscoverLocalExecutable()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(userProfile, "source", "repos", "NaturalCommands", "bin", "Debug", "net10.0-windows", "NaturalCommands.exe"),
            Path.Combine(userProfile, "source", "repos", "NaturalCommands", "bin", "Release", "net10.0-windows", "NaturalCommands.exe"),
            Path.Combine(userProfile, "source", "repos", "NaturalCommands", "bin", "Release", "net10.0-windows", "win-x64", "NaturalCommands.exe"),
            Path.Combine(userProfile, "source", "repos", "NaturalCommands", "bin", "Release", "net10.0-windows", "publish", "NaturalCommands.exe"),
            Path.Combine(userProfile, "source", "repos", "NaturalCommands", "bin", "Release", "net10.0-windows", "win-x64", "publish", "NaturalCommands.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string NormalizeLeadingNatural(string commandText)
    {
        var trimmed = commandText.Trim();
        if (trimmed.StartsWith("natural ", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed["natural ".Length..].Trim();
        }

        if (string.Equals(trimmed, "natural", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return trimmed;
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

internal sealed record NaturalCommandExecutionResult(
    bool Success,
    string Message,
    int? ExitCode = null,
    bool TimedOut = false);
