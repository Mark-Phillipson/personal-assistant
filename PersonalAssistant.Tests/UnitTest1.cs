using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PersonalAssistant.Tests;

public class DatabasePhaseThreeTests
{
    [Fact]
    public void SqlSecurityHelper_AllowsSelectAndRejectsWrites()
    {
        Assert.True(SqlSecurityHelper.IsSelectQueryOnly("SELECT * FROM T"));
        Assert.True(SqlSecurityHelper.IsSelectQueryOnly("  WITH cte AS (SELECT 1 as X) SELECT * FROM cte "));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("INSERT INTO T VALUES (1)"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("DELETE FROM T"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("DROP TABLE T"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("SELECT * FROM T; DROP TABLE T;"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly(""));
    }

    [Fact]
    public async Task GenericDatabaseService_UnknownAliasAndUnsafeSqlBehaviorAsync()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempFile }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE People (Id INTEGER PRIMARY KEY, Name TEXT);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO People (Name) VALUES ('Alice'), ('Bob');";
                cmd.ExecuteNonQuery();
            }

            var source = new DatabaseSource("test", DatabaseProviderType.SQLite, tempFile, "TempSqlite", "main", true);
            var registry = new DatabaseRegistry(new[] { source });
            var service = new GenericDatabaseService(registry);

            var unknownTables = (await service.ListTablesAsync("unknown")).ToList();
            Assert.Equal(0, unknownTables.Count);

            var filtered = (await service.QueryTableAsync("test", "People", "Name = 'Alice'", 10)).ToList();
            Assert.Equal(1, filtered.Count);
            Assert.Equal("Alice", filtered[0]["Name"]);

            var selectResults = (await service.ExecuteReadOnlySqlAsync("test", "SELECT Name FROM People ORDER BY Name", 10)).ToList();
            Assert.Equal(2, selectResults.Count);
            Assert.Equal("Alice", selectResults[0]["Name"]);

            var unsafeSql = (await service.ExecuteReadOnlySqlAsync("test", "DROP TABLE People", 10)).ToList();
            Assert.Equal(0, unsafeSql.Count);
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
                // safe to ignore temp file cleanup issues during tests.
            }
        }
    }

    [Fact]
    public async Task SqliteDatabaseProvider_QueryAndSqlExecuteWorkAsync()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // create database file
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempFile }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE People (Id INTEGER PRIMARY KEY, Name TEXT);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO People (Name) VALUES ('Alice'), ('Bob');";
                cmd.ExecuteNonQuery();
            }

            var source = new DatabaseSource("test", DatabaseProviderType.SQLite, tempFile, "TempSqlite", "main", true);
            var provider = new SqliteDatabaseProvider(source);

            var tables = (await provider.ListTablesAsync()).ToList();
            Assert.Contains("People", tables);

            var schema = (await provider.GetTableSchemaAsync("People")).ToList();
            Assert.Contains(schema, c => c.Name == "Id" && c.IsPrimaryKey);
            Assert.Contains(schema, c => c.Name == "Name");

            var count = await provider.CountRowsAsync("People");
            Assert.Equal(2, count);

            var preview = (await provider.PreviewRowsAsync("People", 2)).ToList();
            Assert.Equal(2, preview.Count);
            Assert.Equal("Alice", preview[0]["Name"]);

            var query = (await provider.QueryTableAsync("People", "Name = 'Bob'", 10)).ToList();
            Assert.Equal(1, query.Count);
            Assert.Equal("Bob", query[0]["Name"]);

            var sqlRows = (await provider.ExecuteReadOnlySqlAsync("SELECT Name FROM People ORDER BY Name", 10)).ToList();
            Assert.Equal(2, sqlRows.Count);
            Assert.Equal("Alice", sqlRows[0]["Name"]);

            var dangerous = await provider.ExecuteReadOnlySqlAsync("DROP TABLE People", 10);
            Assert.Equal(0, dangerous.Count());
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
                // safe to ignore temp file cleanup issues during tests.
            }
        }
    }
}
