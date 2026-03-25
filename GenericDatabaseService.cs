using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal sealed class GenericDatabaseService
{
    private readonly DatabaseRegistry _registry;
    private readonly ConcurrentDictionary<string, IDatabaseProvider> _providers;

    public GenericDatabaseService(DatabaseRegistry registry)
    {
        _registry = registry;
        _providers = new ConcurrentDictionary<string, IDatabaseProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _registry.Sources)
        {
            switch (source.ProviderType)
            {
                case DatabaseProviderType.SQLite:
                    _providers[source.Alias] = new SqliteDatabaseProvider(source);
                    break;

                case DatabaseProviderType.SqlServer:
                    _providers[source.Alias] = new SqlServerDatabaseProvider(source);
                    break;

                default:
                    // unknown provider types are ignored for now
                    break;
            }
        }
    }

    public IReadOnlyList<DatabaseSource> ListSources() => _registry.Sources;

    public bool TryGetSource(string alias, out DatabaseSource source)
    {
        if (_registry.TryGetSource(alias, out source))
        {
            return true;
        }

        source = default!;
        return false;
    }

    public bool TryGetProvider(string alias, out IDatabaseProvider provider)
    {
        return _providers.TryGetValue(alias ?? string.Empty, out provider!);
    }

    public async Task<IEnumerable<string>> ListTablesAsync(string alias, CancellationToken cancellationToken = default)
    {
        if (!TryGetProvider(alias, out var provider))
        {
            return Enumerable.Empty<string>();
        }

        return await provider.ListTablesAsync(cancellationToken);
    }

    public async Task<IEnumerable<DatabaseColumn>> GetTableSchemaAsync(string alias, string tableName, CancellationToken cancellationToken = default)
    {
        if (!TryGetProvider(alias, out var provider))
        {
            return Enumerable.Empty<DatabaseColumn>();
        }

        return await provider.GetTableSchemaAsync(tableName, cancellationToken);
    }

    public async Task<long> CountRowsAsync(string alias, string tableName, CancellationToken cancellationToken = default)
    {
        if (!TryGetProvider(alias, out var provider))
        {
            return 0;
        }

        return await provider.CountRowsAsync(tableName, cancellationToken);
    }

    public async Task<IEnumerable<IDictionary<string, object?>>> PreviewRowsAsync(string alias, string tableName, int maxRows = 10, CancellationToken cancellationToken = default)
    {
        if (!TryGetProvider(alias, out var provider))
        {
            return Enumerable.Empty<IDictionary<string, object?>>();
        }

        return await provider.PreviewRowsAsync(tableName, maxRows, cancellationToken);
    }

    public async Task<IEnumerable<IDictionary<string, object?>>> QueryTableAsync(string alias, string tableName, string? whereClause = null, int maxRows = 50, CancellationToken cancellationToken = default)
    {
        if (!TryGetProvider(alias, out var provider))
        {
            return Enumerable.Empty<IDictionary<string, object?>>();
        }

        if (string.IsNullOrWhiteSpace(tableName))
        {
            return Enumerable.Empty<IDictionary<string, object?>>();
        }

        return await provider.QueryTableAsync(tableName, whereClause, maxRows, cancellationToken);
    }

    public async Task<IEnumerable<IDictionary<string, object?>>> ExecuteReadOnlySqlAsync(string alias, string sql, int maxRows = 100, CancellationToken cancellationToken = default)
    {
        if (!TryGetProvider(alias, out var provider) || !SqlSecurityHelper.IsSelectQueryOnly(sql))
        {
            return Enumerable.Empty<IDictionary<string, object?>>();
        }

        return await provider.ExecuteReadOnlySqlAsync(sql, maxRows, cancellationToken);
    }

    public async Task<string?> ResolveObjectNameAsync(string alias, string userInput, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userInput) || !TryGetProvider(alias, out _))
        {
            return null;
        }

        var tables = (await ListTablesAsync(alias, cancellationToken)).ToList();
        if (!tables.Any())
        {
            return null;
        }

        var exact = tables.FirstOrDefault(t => string.Equals(t, userInput, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        var normalized = userInput.Trim().ToLowerInvariant();
        var candidate = tables
            .FirstOrDefault(t => t.Trim().ToLowerInvariant() == normalized || t.Trim().ToLowerInvariant().EndsWith($".{normalized}") || t.Trim().ToLowerInvariant().Contains(normalized));

        return candidate;
    }
}
