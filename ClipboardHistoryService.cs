using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

internal sealed class ClipboardHistoryService
{
    private readonly string? _dbPath;
    private readonly int _maxResults;
    private readonly int _retentionDays;
    private bool _initialized = false;
    private ClipboardMonitor? _monitor;

    private ClipboardHistoryService(string? dbPath, int maxResults, int retentionDays)
    {
        _dbPath = dbPath;
        _maxResults = maxResults;
        _retentionDays = retentionDays;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_dbPath) && File.Exists(_dbPath);

    public static ClipboardHistoryService FromEnvironment()
    {
        var dbPath = EnvironmentSettings.ReadOptionalString("CLIPBOARD_HISTORY_DB_PATH")
            ?? "clipboard-history.db";
        var maxResults = EnvironmentSettings.ReadInt(
            "CLIPBOARD_HISTORY_MAX_RESULTS",
            fallback: 50,
            min: 1,
            max: 500);
        var retentionDays = EnvironmentSettings.ReadInt(
            "CLIPBOARD_HISTORY_RETENTION_DAYS",
            fallback: 21,
            min: 7,
            max: 90);

        return new ClipboardHistoryService(dbPath, maxResults, retentionDays);
    }

    public string GetSetupStatusText()
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
            return "CLIPBOARD_HISTORY_DB_PATH is not set. Clipboard history is disabled.";

        if (!File.Exists(_dbPath))
            return $"Clipboard history database will be created at: {_dbPath}";

        return $"Clipboard history is configured at: {_dbPath} (retention: {_retentionDays} days)";
    }

    public async Task AddEntryAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_dbPath) || string.IsNullOrWhiteSpace(content))
        {
            if (string.IsNullOrWhiteSpace(_dbPath))
                Console.WriteLine("[clipboard-history.add] Skipped: _dbPath is not set");
            return;
        }

        content = content.Trim();
        if (content.Length > 100_000)  // Skip very large clipboard content
        {
            Console.WriteLine("[clipboard-history.add] Skipped: content too large");
            return;
        }

        try
        {
            await InitializeDatabaseAsync(cancellationToken);

            var contentHash = ComputeContentHash(content);
            var recentHash = await GetRecentEntryHashAsync(cancellationToken);

            // Skip if same as most recent entry (deduplication)
            if (recentHash == contentHash)
            {
                Console.WriteLine("[clipboard-history.add] Skipped: duplicate of most recent entry");
                return;
            }

            var timestamp = DateTime.UtcNow.ToString("O");  // ISO 8601 format
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(cancellationToken);

            const string sql = """
                INSERT INTO ClipboardHistory (content, contentHash, timestamp)
                VALUES (@content, @hash, @timestamp)
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@content", content);
            cmd.Parameters.AddWithValue("@hash", contentHash);
            cmd.Parameters.AddWithValue("@timestamp", timestamp);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            Console.WriteLine($"[clipboard-history.add] Entry added successfully (hash={contentHash[..8]})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[clipboard-history.error] Failed to add clipboard entry: {ex.Message}");
        }
    }

    public async Task<string> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
            return GetSetupStatusText();

        if (string.IsNullOrWhiteSpace(keyword))
            return "Please provide a search keyword.";

        try
        {
            if (!File.Exists(_dbPath))
                return "Clipboard history is empty.";

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(cancellationToken);

            const string sql = """
                SELECT id, content, timestamp
                FROM ClipboardHistory
                WHERE content LIKE @keyword
                ORDER BY timestamp DESC
                LIMIT @limit
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@keyword", $"%{keyword}%");
            cmd.Parameters.AddWithValue("@limit", _maxResults);

            var results = new StringBuilder();
            var count = 0;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                count++;
                var id = reader.GetInt64(0);
                var content = reader.GetString(1);
                var timestamp = reader.GetString(2);

                // Parse ISO 8601 timestamp and format as readable date/time
                var dt = DateTime.Parse(timestamp).ToLocalTime();
                var timeStr = dt.ToString("yyyy-MM-dd HH:mm:ss");

                // Truncate content to 200 characters
                var preview = content.Length > 200
                    ? content[..200] + "..."
                    : content;

                results.AppendLine($"[{timeStr}] {preview}");
            }

            if (count == 0)
                return $"No clipboard history entries found matching '{keyword}'.";

            return $"Found {count} entry/entries matching '{keyword}':\n{results}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[clipboard-history.error] Failed to search clipboard history: {ex.Message}");
            return $"Error searching clipboard history: {ex.Message}";
        }
    }

    public async Task<string> GetTodayEntriesAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
            return GetSetupStatusText();

        try
        {
            if (!File.Exists(_dbPath))
                return "Clipboard history is empty. No entries for today.";

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(cancellationToken);

            // Get entries from today (local time)
            var todayStart = DateTime.Now.Date.ToUniversalTime().ToString("O");
            var tomorrowStart = DateTime.Now.AddDays(1).Date.ToUniversalTime().ToString("O");

            const string sql = """
                SELECT id, content, timestamp
                FROM ClipboardHistory
                WHERE timestamp >= @todayStart AND timestamp < @tomorrowStart
                ORDER BY timestamp DESC
                LIMIT @limit
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@todayStart", todayStart);
            cmd.Parameters.AddWithValue("@tomorrowStart", tomorrowStart);
            cmd.Parameters.AddWithValue("@limit", _maxResults);

            var results = new StringBuilder();
            var count = 0;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                count++;
                var id = reader.GetInt64(0);
                var content = reader.GetString(1);
                var timestamp = reader.GetString(2);

                // Parse ISO 8601 timestamp and format as readable date/time
                var dt = DateTime.Parse(timestamp).ToLocalTime();
                var timeStr = dt.ToString("HH:mm:ss");

                // Truncate content to 200 characters
                var preview = content.Length > 200
                    ? content[..200] + "..."
                    : content;

                results.AppendLine($"[{timeStr}] {preview}");
            }

            if (count == 0)
                return "No clipboard entries recorded for today yet.";

            return $"Today's clipboard ({count} entries):\n{results}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[clipboard-history.error] Failed to get today's entries: {ex.Message}");
            return $"Error retrieving today's clipboard entries: {ex.Message}";
        }
    }

    public void StartMonitoring()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(_dbPath))
            return;

        if (_monitor == null)
        {
            _monitor = new ClipboardMonitor(this);
            _monitor.Start();
        }
    }

    public void StopMonitoring()
    {
        if (_monitor != null)
        {
            _monitor.Stop();
            _monitor = null;
        }
    }

    public async Task CleanupOldEntriesAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_dbPath) || !File.Exists(_dbPath))
            return;

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(cancellationToken);

            var cutoffDate = DateTime.UtcNow.AddDays(-_retentionDays).ToString("O");

            const string sql = """
                DELETE FROM ClipboardHistory
                WHERE timestamp < @cutoff
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

            var deletedCount = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (deletedCount > 0)
            {
                Console.WriteLine($"[clipboard-history] Cleaned up {deletedCount} old entries (> {_retentionDays} days)");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[clipboard-history.error] Failed to cleanup old entries: {ex.Message}");
        }
    }

    private async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || string.IsNullOrWhiteSpace(_dbPath))
            return;

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(cancellationToken);

            const string sql = """
                CREATE TABLE IF NOT EXISTS ClipboardHistory (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content TEXT NOT NULL,
                    contentHash TEXT NOT NULL,
                    timestamp TEXT NOT NULL
                )
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[clipboard-history.error] Failed to initialize database: {ex.Message}");
        }
    }

    private static string ComputeContentHash(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private async Task<string?> GetRecentEntryHashAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_dbPath) || !File.Exists(_dbPath))
            return null;

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(cancellationToken);

            const string sql = """
                SELECT contentHash
                FROM ClipboardHistory
                ORDER BY id DESC
                LIMIT 1
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Monitor for Windows clipboard changes and record them to history.
/// Uses WM_CLIPBOARDUPDATE to detect when clipboard content changes.
/// </summary>
internal sealed class ClipboardMonitor
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly ClipboardHistoryService _historyService;
    private Thread? _monitorThread;
    private volatile bool _running;
    private string? _lastContent;

    public ClipboardMonitor(ClipboardHistoryService historyService)
    {
        _historyService = historyService;
    }

    public void Start()
    {
        if (_running) return;

        _running = true;
        _monitorThread = new Thread(MonitorLoop)
        {
            Name = "ClipboardMonitor",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _monitorThread.Start();
        Console.WriteLine("[clipboard-history.monitor] Clipboard monitor started (polling every 500ms)");
    }

    public void Stop()
    {
        _running = false;
        _monitorThread?.Join(TimeSpan.FromSeconds(5));
        _monitorThread = null;
        Console.WriteLine("[clipboard-history] Clipboard monitor stopped");
    }

    private void MonitorLoop()
    {
        while (_running)
        {
            try
            {
                // Poll Windows clipboard every 500ms for changes
                var currentContent = TryGetClipboardText();

                if (!string.IsNullOrEmpty(currentContent) && currentContent != _lastContent)
                {
                    _lastContent = currentContent;
                    Console.WriteLine($"[clipboard-history.monitor] Clipboard content detected ({currentContent.Length} chars), adding to history...");
                    // Fire and forget - add entry asynchronously
                    _ = _historyService.AddEntryAsync(currentContent);
                }

                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[clipboard-history.monitor] Error in monitoring loop: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private static string? TryGetClipboardText()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return null;

            // Use System.Windows.Forms for clipboard access
            // Fall back to PowerShell if Forms isn't available
            try
            {
                return GetClipboardViaPowerShell();
            }
            catch
            {
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? GetClipboardViaPowerShell()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-Clipboard -Raw\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                if (process == null)
                {
                    Console.WriteLine("[clipboard-history.monitor] Failed to start pwsh process");
                    return null;
                }

                var timeout = TimeSpan.FromSeconds(2);
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    process.Kill();
                    Console.WriteLine("[clipboard-history.monitor] PowerShell clipboard read timed out");
                    return null;
                }

                if (process.ExitCode != 0)
                {
                    var stderr = process.StandardError.ReadToEnd();
                    Console.WriteLine($"[clipboard-history.monitor] PowerShell failed with exit code {process.ExitCode}: {stderr}");
                    return null;
                }

                var output = process.StandardOutput.ReadToEnd()?.Trim();
                return string.IsNullOrEmpty(output) ? null : output;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[clipboard-history.monitor] PowerShell exception: {ex.Message}");
            return null;
        }
    }
}
