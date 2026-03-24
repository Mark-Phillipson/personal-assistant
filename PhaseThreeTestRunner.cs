using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

internal static class PhaseThreeTestRunner
{
    public static void RunAll()
    {
        TestSqlSecurityHelper();
        TestSqliteDatabaseProviderWithTempFile();
        Console.WriteLine("Phase 3 tests passed.");
    }

#pragma warning disable CS8602
    private static void TestSqlSecurityHelper()
    {
        if (!SqlSecurityHelper.IsSelectQueryOnly("SELECT * FROM T")) throw new InvalidOperationException("SELECT should be allowed");
        if (!SqlSecurityHelper.IsSelectQueryOnly("WITH cte AS (SELECT 1 AS X) SELECT * FROM cte")) throw new InvalidOperationException("WITH/SELECT query should be allowed");
        if (SqlSecurityHelper.IsSelectQueryOnly("INSERT INTO T VALUES (1)")) throw new InvalidOperationException("INSERT should be disallowed");
        if (SqlSecurityHelper.IsSelectQueryOnly("DELETE FROM T")) throw new InvalidOperationException("DELETE should be disallowed");
        if (SqlSecurityHelper.IsSelectQueryOnly("DROP TABLE T")) throw new InvalidOperationException("DROP should be disallowed");
        if (SqlSecurityHelper.IsSelectQueryOnly("SELECT * FROM T; DROP TABLE T;")) throw new InvalidOperationException("Batch with DROPs should be disallowed");
        if (SqlSecurityHelper.IsSelectQueryOnly(string.Empty)) throw new InvalidOperationException("Empty SQL should be disallowed");
    }
#pragma warning restore CS8602

    private static void TestSqliteDatabaseProviderWithTempFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempFile }.ToString()))
            {
                conn.Open();
                ExecuteNonQuery(conn, "CREATE TABLE People (Id INTEGER PRIMARY KEY, Name TEXT);");
                ExecuteNonQuery(conn, "INSERT INTO People (Name) VALUES ('Alice'), ('Bob');");
            }

            var source = new DatabaseSource("test", DatabaseProviderType.SQLite, tempFile, "TempSqlite", "main", true);
            var provider = new SqliteDatabaseProvider(source);

            var tables = provider.ListTablesAsync().GetAwaiter().GetResult().ToList();
            if (!tables.Contains("People")) throw new InvalidOperationException("People table expected");

            var schema = provider.GetTableSchemaAsync("People").GetAwaiter().GetResult().ToList();
            if (!schema.Any(c => c.Name == "Id" && c.IsPrimaryKey)) throw new InvalidOperationException("Id primary key expected");

            var count = provider.CountRowsAsync("People").GetAwaiter().GetResult();
            if (count != 2) throw new InvalidOperationException($"Expected 2 rows, got {count}");

            var preview = provider.PreviewRowsAsync("People", 2).GetAwaiter().GetResult().ToList();
            if (preview.Count != 2) throw new InvalidOperationException("Expected 2 preview rows");

            var query = provider.QueryTableAsync("People", "Name = 'Bob'", 10).GetAwaiter().GetResult().ToList();
            if (query.Count != 1 || query[0]["Name"].ToString() != "Bob") throw new InvalidOperationException("Expected Bob row");

            var sqlRows = provider.ExecuteReadOnlySqlAsync("SELECT Name FROM People ORDER BY Name", 10).GetAwaiter().GetResult().ToList();
            if (sqlRows.Count != 2 || sqlRows[0]["Name"].ToString() != "Alice") throw new InvalidOperationException("Query result ordering expected");

            var dangerous = provider.ExecuteReadOnlySqlAsync("DROP TABLE People", 10).GetAwaiter().GetResult();
            if (dangerous.Any()) throw new InvalidOperationException("Dangerous query should be blocked");
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
                // If file is locked by lingering SQLite handles, ignore cleanup failure in test runner.
            }
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

