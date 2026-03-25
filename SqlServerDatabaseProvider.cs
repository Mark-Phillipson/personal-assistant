using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

internal sealed class SqlServerDatabaseProvider : IDatabaseProvider
{
    public DatabaseSource Source { get; }

    public SqlServerDatabaseProvider(DatabaseSource source)
    {
        Source = source;
    }

    private string ResolveConnectionString() => Source.ConnectionString;

    private static string QuoteIdentifier(string identifier)
    {
        return "[" + identifier.Replace("]", "]]" ) + "]";
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new SqlConnection(ResolveConnectionString());
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
        const string sql = @"SELECT TABLE_SCHEMA + '.' + TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE IN ('BASE TABLE','VIEW') ORDER BY TABLE_SCHEMA, TABLE_NAME";
        var results = new List<string>();

        await using var conn = new SqlConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, conn);

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

        var parts = tableName.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : parts[0];

        const string sql = @"SELECT c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, 
                                    CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IsPrimaryKey
                             FROM INFORMATION_SCHEMA.COLUMNS c
                             LEFT JOIN (
                                 SELECT k.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS t
                                 JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k ON t.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                                 WHERE t.TABLE_SCHEMA = @schema AND t.TABLE_NAME = @table AND t.CONSTRAINT_TYPE = 'PRIMARY KEY'
                             ) pk ON pk.COLUMN_NAME = c.COLUMN_NAME
                             WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
                             ORDER BY c.ORDINAL_POSITION";

        var results = new List<DatabaseColumn>();
        await using var conn = new SqlConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var dataType = reader.GetString(1);
            var isNullable = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase);
            var pk = reader.GetInt32(3) == 1;
            results.Add(new DatabaseColumn(name, dataType, isNullable, pk));
        }

        return results;
    }

    public async Task<long> CountRowsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return 0;

        string query;
        var parts = tableName.Split('.', 2);
        if (parts.Length == 2)
        {
            query = $"SELECT COUNT(*) FROM {QuoteIdentifier(parts[0])}.{QuoteIdentifier(parts[1])};";
        }
        else
        {
            query = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)};";
        }

        await using var conn = new SqlConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(query, conn);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long l ? l : result is int i ? i : 0;
    }

    public async Task<IEnumerable<IDictionary<string, object?>>> PreviewRowsAsync(string tableName, int maxRows = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName) || maxRows <= 0)
            return Array.Empty<IDictionary<string, object?>>();

        var qualifier = tableName;
        var parts = tableName.Split('.', 2);
        if (parts.Length == 2)
            qualifier = $"{QuoteIdentifier(parts[0])}.{QuoteIdentifier(parts[1])}";

        var query = $"SELECT TOP (@limit) * FROM {qualifier};";

        var rows = new List<IDictionary<string, object?>>();
        await using var conn = new SqlConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(query, conn);
        cmd.Parameters.AddWithValue("@limit", maxRows);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return rows;
    }

    public async Task<IEnumerable<IDictionary<string, object?>>> QueryTableAsync(string tableName, string? whereClause, int maxRows = 50, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName) || maxRows <= 0)
            return Array.Empty<IDictionary<string, object?>>();

        var qualifier = tableName;
        var parts = tableName.Split('.', 2);
        if (parts.Length == 2)
            qualifier = $"{QuoteIdentifier(parts[0])}.{QuoteIdentifier(parts[1])}";

        var whereExpression = string.IsNullOrWhiteSpace(whereClause) ? string.Empty : " WHERE " + whereClause;
        var query = $"SELECT TOP (@limit) * FROM {qualifier}{whereExpression};";

        var rows = new List<IDictionary<string, object?>>();
        await using var conn = new SqlConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(query, conn);
        cmd.Parameters.AddWithValue("@limit", maxRows);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return rows;
    }

    public async Task<IEnumerable<IDictionary<string, object?>>> ExecuteReadOnlySqlAsync(string sql, int maxRows = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql) || !SqlSecurityHelper.IsSelectQueryOnly(sql))
            return Array.Empty<IDictionary<string, object?>>();

        var query = sql.Trim();
        if (!query.EndsWith(";"))
            query += ";";

        var rows = new List<IDictionary<string, object?>>();
        await using var conn = new SqlConnection(ResolveConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(query, conn);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rowCount = 0;
        while (await reader.ReadAsync(cancellationToken) && rowCount < maxRows)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
            rowCount++;
        }

        return rows;
    }
}
