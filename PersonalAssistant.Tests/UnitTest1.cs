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
            Assert.Empty(unknownTables);

            var filtered = (await service.QueryTableAsync("test", "People", "Name = 'Alice'", 10)).ToList();
            Assert.Single(filtered);
            Assert.Equal("Alice", filtered[0]["Name"]);

            var selectResults = (await service.ExecuteReadOnlySqlAsync("test", "SELECT Name FROM People ORDER BY Name", 10)).ToList();
            Assert.Equal(2, selectResults.Count);
            Assert.Equal("Alice", selectResults[0]["Name"]);

            var unsafeSql = (await service.ExecuteReadOnlySqlAsync("test", "DROP TABLE People", 10)).ToList();
            Assert.Empty(unsafeSql);
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
            Assert.Single(query);
            Assert.Equal("Bob", query[0]["Name"]);

            var sqlRows = (await provider.ExecuteReadOnlySqlAsync("SELECT Name FROM People ORDER BY Name", 10)).ToList();
            Assert.Equal(2, sqlRows.Count);
            Assert.Equal("Alice", sqlRows[0]["Name"]);

            var dangerous = await provider.ExecuteReadOnlySqlAsync("DROP TABLE People", 10);
            Assert.Empty(dangerous);
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
    public void DatabaseRegistry_ParsesJsonAndLegacyAlias()
    {
        var json = "{\"databases\":[{\"alias\":\"users\",\"provider\":\"sqlite\",\"connectionString\":\"data.db\",\"displayName\":\"Users DB\",\"defaultSchema\":\"main\",\"readOnly\":true}]}";
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, json);
            Environment.SetEnvironmentVariable("DATABASE_CONFIG_PATH", temp);

            var registry = DatabaseRegistry.FromEnvironment();
            Assert.True(registry.HasSources);
            Assert.True(registry.TryGetSource("users", out var source));
            Assert.Equal("Users DB", source.DisplayName);
            Assert.Equal(DatabaseProviderType.SQLite, source.ProviderType);

            Environment.SetEnvironmentVariable("DATABASE_CONFIG_PATH", null);
            Environment.SetEnvironmentVariable("VOICE_ADMIN_DB_PATH", "C:\\does-not-exist.db");
            var legacyReg = DatabaseRegistry.FromEnvironment();
            Assert.True(legacyReg.HasSources);
            Assert.True(legacyReg.TryGetSource("voiceadmin", out var legacySource));
            Assert.Equal(DatabaseProviderType.SQLite, legacySource.ProviderType);
        }
        finally
        {
            File.Delete(temp);
            Environment.SetEnvironmentVariable("DATABASE_CONFIG_PATH", null);
            Environment.SetEnvironmentVariable("VOICE_ADMIN_DB_PATH", null);
        }
    }

    [Fact]
    public void EnvironmentSettings_ReadOptionalString_RemovesSurroundingQuotes()
    {
        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INSTANCES", "\"localhost\"");
        var value = EnvironmentSettings.ReadOptionalString("DATABASE_SQLSERVER_INSTANCES");
        Assert.Equal("localhost", value);

        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INSTANCES", "'localhost'");
        value = EnvironmentSettings.ReadOptionalString("DATABASE_SQLSERVER_INSTANCES");
        Assert.Equal("localhost", value);

        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INSTANCES", null);
    }

    [Fact]
    public void SqlSecurityHelper_RejectsDangerousSqlComprehensively()
    {
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("INSERT INTO T VALUES(1)"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("UPDATE T SET A=1"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("DELETE FROM T"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("DROP TABLE T"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("CREATE TABLE T(Id INT)"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("ALTER TABLE T ADD COL INT"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("EXEC sp_help"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("USE master"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("SELECT * FROM A; DROP TABLE A;"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("SELECT 1; SELECT 2"));
        Assert.True(SqlSecurityHelper.IsSelectQueryOnly(" SELECT * FROM A; "));
        Assert.True(SqlSecurityHelper.IsSelectQueryOnly("WITH cte AS (SELECT 1 AS x) SELECT * FROM cte"));
    }

    [Fact]
    public async Task SqliteDatabaseProvider_HandlesQuotedNamesAndMixedCaseAsync()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempFile }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE [Mixed Case Table] ([Id] INTEGER PRIMARY KEY, [Value] TEXT);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO [Mixed Case Table] ([Value]) VALUES ('A'), ('B');";
                cmd.ExecuteNonQuery();
            }

            var source = new DatabaseSource("testname", DatabaseProviderType.SQLite, tempFile, "TempSqlite", "main", true);
            var provider = new SqliteDatabaseProvider(source);
            var tables = (await provider.ListTablesAsync()).ToList();
            Assert.Contains("Mixed Case Table", tables);

            var schema = (await provider.GetTableSchemaAsync("Mixed Case Table")).ToList();
            Assert.Contains(schema, c => c.Name == "Id" && c.IsPrimaryKey);
            Assert.Contains(schema, c => c.Name == "Value");

            var count = await provider.CountRowsAsync("Mixed Case Table");
            Assert.Equal(2, count);

            var preview = (await provider.PreviewRowsAsync("Mixed Case Table", 5)).ToList();
            Assert.Equal(2, preview.Count);
            Assert.Equal("A", preview[0]["Value"]);

            var query = (await provider.QueryTableAsync("Mixed Case Table", "Value = 'B'", 10)).ToList();
            Assert.Single(query);
            Assert.Equal("B", query[0]["Value"]);
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void DatabaseRegistry_DiscoverSqlServerDatabases_DoesNotThrow()
    {
        var sources = DatabaseRegistry.DiscoverSqlServerDatabases().ToList();
        Assert.NotNull(sources);
    }

    [Fact]
    public void DatabaseRegistry_DiscoverSqlServerDatabases_SkipsLocalDbByDefault()
    {
        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INCLUDE_LOCALDB", "false");
        var sources = DatabaseRegistry.DiscoverSqlServerDatabases().ToList();
        Assert.DoesNotContain(sources, s => s.Alias.Contains("localdb", StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INCLUDE_LOCALDB", null);
    }

    [Fact]
    public async Task SqlServerDatabaseProvider_RequiresReachableServerAsync()
    {
        var localDbName = "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=master;Integrated Security=True;";
        var source = new DatabaseSource("mssql", DatabaseProviderType.SqlServer, localDbName, "LocalDb", "dbo", true);
        var provider = new SqlServerDatabaseProvider(source);

        try
        {
            var available = await provider.IsAvailableAsync();
            if (!available)
            {
                return;
            }

            var tables = (await provider.ListTablesAsync()).ToList();
            Assert.NotNull(tables);
        }
        catch (Exception ex)
        {
            Assert.Fail("SqlServerDatabaseProvider test failed; ensure LocalDB is installed and available. " + ex.Message);
        }
    }
}
