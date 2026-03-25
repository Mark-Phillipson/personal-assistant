using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

internal interface IDatabaseProvider
{
    DatabaseSource Source { get; }

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<IEnumerable<string>> ListTablesAsync(CancellationToken cancellationToken = default);

    Task<IEnumerable<DatabaseColumn>> GetTableSchemaAsync(string tableName, CancellationToken cancellationToken = default);

    Task<long> CountRowsAsync(string tableName, CancellationToken cancellationToken = default);

    Task<IEnumerable<IDictionary<string, object?>>> PreviewRowsAsync(string tableName, int maxRows = 10, CancellationToken cancellationToken = default);

    Task<IEnumerable<IDictionary<string, object?>>> QueryTableAsync(string tableName, string? whereClause, int maxRows = 50, CancellationToken cancellationToken = default);

    Task<IEnumerable<IDictionary<string, object?>>> ExecuteReadOnlySqlAsync(string sql, int maxRows = 100, CancellationToken cancellationToken = default);
}
