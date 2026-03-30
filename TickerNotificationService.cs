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
        if (string.IsNullOrEmpty(executable))
        {
            executable = "NaturalCommands.exe";
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

        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            Arguments = $"ticker-file \"{tempFile}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        try
        {
            using var process = Process.Start(psi);
            // not awaiting process completion; overlay is managed in NaturalCommands process
        }
        catch (Exception ex)
        {
            // revert if the process couldn't start
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

    private static string FormatLine(TickerMessage m)
    {
        var trimmed = m.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return $"info:"
;
        return $"{m.Category.ToString().ToLowerInvariant()}:{trimmed}";
    }
}
