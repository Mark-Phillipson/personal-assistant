using System.Reflection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PersonalAssistant.Tests;

public sealed class ClipboardHistoryServiceTests
{
    [Fact]
    public async Task AddEntryAsync_PreservesUnicodeContentInSqlite()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "personal-assistant-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var dbPath = Path.Combine(tempDirectory, "clipboard-history.db");

        ClipboardHistoryService? service = null;

        try
        {
            service = CreateService(dbPath, maxResults: 50, retentionDays: 21);
            var originalContent = "Formulaire échantillons Français — déjà vu 🧪 Ελληνικά";

            await service.AddEntryAsync(originalContent);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            await connection.OpenAsync();

            await using var command = new SqliteCommand(
                "SELECT content FROM ClipboardHistory ORDER BY id DESC LIMIT 1;",
                connection);
            var storedContent = (string?)await command.ExecuteScalarAsync();

            Assert.NotNull(storedContent);
            Assert.Equal(originalContent, storedContent);
            Assert.DoesNotContain('\uFFFD', storedContent);
        }
        finally
        {
            service?.StopMonitoring();
            if (Directory.Exists(tempDirectory))
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // SQLite can briefly keep handles open; cleanup is best effort for test temp files.
                }
            }
        }
    }

    [Fact]
    public async Task AddEntryAsync_RecopyingExistingContentMovesItToTopWithoutDuplicates()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "personal-assistant-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var dbPath = Path.Combine(tempDirectory, "clipboard-history.db");

        ClipboardHistoryService? service = null;

        try
        {
            service = CreateService(dbPath, maxResults: 50, retentionDays: 21);
            const string content = "A clipboard item copied more than once";

            await service.AddEntryAsync(content);

            long originalId;
            string originalTimestamp;
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = new SqliteCommand(
                    "SELECT id, timestamp FROM ClipboardHistory WHERE contentHash = @hash;",
                    connection);
                command.Parameters.AddWithValue("@hash", Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                originalId = reader.GetInt64(0);
                originalTimestamp = reader.GetString(1);
            }

            await Task.Delay(20);
            await service.AddEntryAsync(content);

            await using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            await verifyConnection.OpenAsync();

            await using var verifyCommand = new SqliteCommand(
                "SELECT id, timestamp, COUNT(*) OVER () FROM ClipboardHistory WHERE contentHash = @hash ORDER BY id DESC LIMIT 1;",
                verifyConnection);
            verifyCommand.Parameters.AddWithValue("@hash", Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
            await using var verifyReader = await verifyCommand.ExecuteReaderAsync();

            Assert.True(await verifyReader.ReadAsync());
            Assert.True(verifyReader.GetInt64(0) > originalId);
            Assert.True(DateTime.Parse(verifyReader.GetString(1)) > DateTime.Parse(originalTimestamp));
            Assert.Equal(1, verifyReader.GetInt64(2));
        }
        finally
        {
            service?.StopMonitoring();
            if (Directory.Exists(tempDirectory))
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // SQLite can briefly keep handles open; cleanup is best effort for test temp files.
                }
            }
        }
    }

    private static ClipboardHistoryService CreateService(string dbPath, int maxResults, int retentionDays)
    {
        var constructor = typeof(ClipboardHistoryService).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(string), typeof(int), typeof(int) },
            modifiers: null);

        Assert.NotNull(constructor);

        var instance = constructor!.Invoke(new object[] { dbPath, maxResults, retentionDays });
        return Assert.IsType<ClipboardHistoryService>(instance);
    }
}
