using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

public enum TickerCategory
{
    Info,
    Warning,
    Critical,
    Success
}

public record TickerMessage(string Text, TickerCategory Category = TickerCategory.Info);

internal sealed class TickerNotificationService
{
    private readonly Queue<TickerMessage> _pending = new();
    private readonly string _executable;
    private readonly int _autoFlushThreshold;
    private readonly object _lock = new();

    private TickerNotificationService(string executable, int autoFlushThreshold)
    {
        _executable = executable;
        _autoFlushThreshold = Math.Max(1, autoFlushThreshold);
    }

    public static TickerNotificationService FromEnvironment()
    {
        var executable = Environment.GetEnvironmentVariable("NATURAL_COMMANDS_EXECUTABLE")?.Trim();

        // If not explicitly configured, prefer the locally-built NaturalCommands exe in the sibling repo
        if (string.IsNullOrEmpty(executable))
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            var candidates = new[]
            {
                Path.Combine(repoRoot, "NaturalCommands", "bin", "Debug", "net10.0-windows", "NaturalCommands.exe"),
                Path.Combine(repoRoot, "NaturalCommands", "bin", "Release", "net10.0-windows", "NaturalCommands.exe"),
                Path.Combine(repoRoot, "NaturalCommands", "bin", "Release", "net10.0-windows", "publish", "NaturalCommands.exe"),
                Path.Combine(repoRoot, "NaturalCommands", "NaturalCommands.exe"),
                "NaturalCommands.exe"
            };

            executable = candidates.FirstOrDefault(File.Exists) ?? "NaturalCommands.exe";
        }

        var thresholdText = Environment.GetEnvironmentVariable("TICKER_AUTO_FLUSH_THRESHOLD");
        var threshold = 5;
        if (!string.IsNullOrEmpty(thresholdText) && int.TryParse(thresholdText, out var parsed))
        {
            threshold = parsed;
        }

        return new TickerNotificationService(executable, threshold);
    }

    public void Enqueue(string message, TickerCategory category = TickerCategory.Info)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        lock (_lock)
        {
            _pending.Enqueue(new TickerMessage(message.Trim(), category));

            if (category == TickerCategory.Critical || _pending.Count >= _autoFlushThreshold)
            {
                _ = FlushAsync();
            }
        }
    }

    public async Task EnqueueAndFlushAsync(string message, TickerCategory category = TickerCategory.Info)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        lock (_lock)
        {
            _pending.Enqueue(new TickerMessage(message.Trim(), category));
        }

        await FlushAsync();
    }

    public async Task FlushAsync()
    {
        List<TickerMessage> batch;

        lock (_lock)
        {
            if (_pending.Count == 0)
                return;

            batch = new List<TickerMessage>(_pending);
            _pending.Clear();
        }

        var lines = batch.ConvertAll(FormatLine);

        var tempFile = Path.Combine(Path.GetTempPath(), $"assistant_ticker_{Guid.NewGuid():N}.txt");

        await File.WriteAllLinesAsync(tempFile, lines, Encoding.UTF8);

        try
        {
            // If a resident NaturalCommands listen-mode is running, deliver the payload via a known LocalAppData file instead of starting a new process.
            var running = Process.GetProcessesByName("NaturalCommands");
            if (running != null && running.Length > 0)
            {
                try
                {
                    var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaturalCommands");
                    Directory.CreateDirectory(baseDir);
                    var payloadPath = Path.Combine(baseDir, ".ticker_payload");
                    var tempPath = payloadPath + ".tmp";
                    await File.WriteAllLinesAsync(tempPath, lines, Encoding.UTF8);
                    // Atomically move
                    if (File.Exists(payloadPath)) File.Delete(payloadPath);
                    File.Move(tempPath, payloadPath);
                    NaturalCommandsLog($"Delivered ticker payload to resident listener: {payloadPath}");
                    return;
                }
                catch (Exception ex)
                {
                    NaturalCommandsLog($"Failed to deliver ticker payload to listener: {ex.Message} — falling back to process start");
                    // fall through to process start
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = _executable,
                Arguments = $"ticker-file \"{tempFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            NaturalCommandsLog($"Starting ticker process: {psi.FileName} {psi.Arguments}");
            using var process = Process.Start(psi);
            if (process == null)
            {
                NaturalCommandsLog("Process.Start returned null — unable to start NaturalCommands executable.");
            }
            else
            {
                NaturalCommandsLog($"Started NaturalCommands process with PID {process.Id}");
            }
        }
        catch (Exception ex)
        {
            NaturalCommandsLog($"Failed to start or deliver NaturalCommands: {ex.Message}");
            // revert if the process couldn't start or delivery failed
            lock (_lock)
            {
                foreach (var item in batch)
                {
                    _pending.Enqueue(item);
                }
            }

            throw new InvalidOperationException($"Failed to start ticker: {ex.Message}", ex);
        }

    }

    private static void NaturalCommandsLog(string message)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "personal_assistant_ticker.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] {message}\n");
        }
        catch { }
        }

    private static string FormatLine(TickerMessage m)
    {
        var trimmed = m.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return $"info:"
;
        return $"{m.Category.ToString().ToLowerInvariant()}:{trimmed}";
    }
}
