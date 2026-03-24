using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

internal sealed class SqliteDatabaseProvider : IDatabaseProvider
{
    public DatabaseSource Source { get; }

    public SqliteDatabaseProvider(DatabaseSource source)
    {
        Source = source;
    }

    private string ResolveConnectionString()
    {
        if (Source.ConnectionString.Contains(";", StringComparison.Ordinal))
        {
            return Source.ConnectionString;
        }

        var path = Source.ConnectionString;
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new SqliteConnection(ResolveConnectionString());
            await conn.OpenAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IEnumerable<string>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name";

        var results = new List<string>();

        await using var conn = new SqliteConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqliteCommand(sql, conn);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public async Task<IEnumerable<DatabaseColumn>> GetTableSchemaAsync(string tableName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return Array.Empty<DatabaseColumn>();

        var quotedTable = QuoteIdentifier(tableName);
        var query = $"PRAGMA table_info({quotedTable});";

        var results = new List<DatabaseColumn>();

        await using var conn = new SqliteConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqliteCommand(query, conn);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var notNull = reader.GetBoolean(3);
            var pk = reader.GetInt32(5) > 0;

            results.Add(new DatabaseColumn(name, type, !notNull, pk));
        }

        return results;
    }

    public async Task<long> CountRowsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return 0;

        var query = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)};";

        await using var conn = new SqliteConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqliteCommand(query, conn);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is long l)
            return l;

        if (result is int i)
            return i;

        return 0;
    }

    public async Task<IEnumerable<IDictionary<string, object?>>> PreviewRowsAsync(string tableName, int maxRows = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName) || maxRows <= 0)
        {
            return Array.Empty<IDictionary<string, object?>>();
        }

        var query = $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT @limit;";
        var results = new List<IDictionary<string, object?>>();

        await using var conn = new SqliteConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@limit", maxRows);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            results.Add(row);
        }

        return results;
    }

    public async Task<IEnumerable<IDictionary<string, object?>>> QueryTableAsync(string tableName, string? whereClause, int maxRows = 50, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName) || maxRows <= 0)
        {
            return Array.Empty<IDictionary<string, object?>>();
        }

        var whereExpression = string.IsNullOrWhiteSpace(whereClause) ? string.Empty : " WHERE " + whereClause;
        var query = $"SELECT * FROM {QuoteIdentifier(tableName)}{whereExpression} LIMIT @limit;";
        var results = new List<IDictionary<string, object?>>();

        await using var conn = new SqliteConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@limit", maxRows);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            results.Add(row);
        }

        return results;
    }

    public async Task<IEnumerable<IDictionary<string, object?>>> ExecuteReadOnlySqlAsync(string sql, int maxRows = 100, CancellationToken cancellationToken = default)
    {
        if (!SqlSecurityHelper.IsSelectQueryOnly(sql) || string.IsNullOrWhiteSpace(sql))
        {
            return Array.Empty<IDictionary<string, object?>>();
        }

        var query = sql.Trim();
        if (!query.EndsWith(";"))
        {
            query += ";";
        }

        var results = new List<IDictionary<string, object?>>();

        await using var conn = new SqliteConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqliteCommand(query, conn);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var readRows = 0;
        while (await reader.ReadAsync(cancellationToken) && readRows < maxRows)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            results.Add(row);
            readRows++;
        }

        return results;
    }
}
