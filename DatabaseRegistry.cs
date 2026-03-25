using System.Data;
using Microsoft.Data.SqlClient;
using System.Text.Json;

internal sealed class DatabaseRegistry
{
    private readonly Dictionary<string, DatabaseSource> _sources;

    public DatabaseRegistry(IEnumerable<DatabaseSource> sources)
    {
        _sources = sources
            .Where(s => !string.IsNullOrWhiteSpace(s.Alias))
            .GroupBy(s => s.Alias.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DatabaseSource> Sources => _sources.Values.ToList();

    public bool HasSources => _sources.Count > 0;

    public static DatabaseRegistry FromEnvironment()
    {
        var sources = new List<DatabaseSource>();

        var legacyVoiceAdminPath = EnvironmentSettings.ReadOptionalString("VOICE_ADMIN_DB_PATH")
            ?? EnvironmentSettings.ReadOptionalString("VOICE_LAUNCHER_DB_PATH");

        if (!string.IsNullOrWhiteSpace(legacyVoiceAdminPath) && File.Exists(legacyVoiceAdminPath))
        {
            sources.Add(new DatabaseSource(
                Alias: "voiceadmin",
                ProviderType: DatabaseProviderType.SQLite,
                ConnectionString: legacyVoiceAdminPath,
                DisplayName: "Voice Admin (legacy)",
                DefaultSchema: "main",
                ReadOnly: true));
        }

        var configPath = EnvironmentSettings.ReadOptionalString("DATABASE_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            sources.AddRange(LoadFromJsonFile(configPath));
        }
        else
        {
            var configJson = EnvironmentSettings.ReadOptionalString("DATABASE_CONFIG_JSON");
            if (!string.IsNullOrWhiteSpace(configJson))
            {
                sources.AddRange(LoadFromJsonString(configJson));
            }
        }

        // Add a fallback option for local voice admin path if path is set but file does not exist yet
        if (!sources.Any() && !string.IsNullOrWhiteSpace(legacyVoiceAdminPath))
        {
            sources.Add(new DatabaseSource(
                Alias: "voiceadmin",
                ProviderType: DatabaseProviderType.SQLite,
                ConnectionString: legacyVoiceAdminPath,
                DisplayName: "Voice Admin (legacy)",
                DefaultSchema: "main",
                ReadOnly: true));
        }

        // Add local SQL Server discovery sources if enabled by env var (defaults true).
        var sqlDiscoveryEnabled = EnvironmentSettings.ReadBool("DATABASE_SQLSERVER_DISCOVERY", true);
        if (sqlDiscoveryEnabled)
        {
            sources.AddRange(DiscoverSqlServerDatabases());
        }

        return new DatabaseRegistry(sources);
    }

    internal static IEnumerable<DatabaseSource> DiscoverSqlServerDatabases()
    {
        var result = new List<DatabaseSource>();
        try
        {
            static string SanitizeAlias(string value) => new string(value.ToLowerInvariant().Replace(" ", "_").Replace("\\", "_").Replace("/", "_").Replace(".", "_").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());

            var instanceCandidates = new List<string>();

            var explicitInstances = EnvironmentSettings.ReadOptionalString("DATABASE_SQLSERVER_INSTANCES");
            if (!string.IsNullOrWhiteSpace(explicitInstances))
            {
                instanceCandidates.AddRange(explicitInstances
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            // Always try the local host default instance first.
            instanceCandidates.AddRange(new[]
            {
                ".",
                "localhost",
                "127.0.0.1",
                "(local)",
                Environment.MachineName
            });

            var includeLocalDb = EnvironmentSettings.ReadBool("DATABASE_SQLSERVER_INCLUDE_LOCALDB", false);
            if (includeLocalDb)
            {
                // Named localdb instances are rarely needed, only include when explicitly requested.
                instanceCandidates.AddRange(new[]
                {
                    "(localdb)\\MSSQLLocalDB",
                    "(localdb)\\ProjectsV13"
                });
            }


            instanceCandidates = instanceCandidates
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var instance in instanceCandidates)
            {
                if (string.IsNullOrWhiteSpace(instance))
                {
                    continue;
                }

                var sqlBuilder = new SqlConnectionStringBuilder
                {
                    DataSource = instance,
                    InitialCatalog = "master",
                    IntegratedSecurity = true,
                    ConnectTimeout = 3,
                    Encrypt = false,
                    TrustServerCertificate = true
                };

                var connectionString = sqlBuilder.ToString();

                try
                {
                    using var conn = new SqlConnection(connectionString);
                    conn.Open();

                    using var cmd = conn.CreateCommand();
                    var includeSystem = EnvironmentSettings.ReadBool("DATABASE_SQLSERVER_INCLUDE_SYSTEM", false);
                    cmd.CommandText = includeSystem
                        ? "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name"
                        : "SELECT name FROM sys.databases WHERE state = 0 AND name NOT IN('master','tempdb','model','msdb') ORDER BY name";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var dbName = reader.GetString(0);
                        var alias = $"sqlserver_{SanitizeAlias(instance)}_{SanitizeAlias(dbName)}";
                        if (string.IsNullOrWhiteSpace(alias))
                        {
                            continue;
                        }

                        // Optionally skip LocalDB-derived db sources in the UI.
                        var showLocalDb = EnvironmentSettings.ReadBool("DATABASE_SQLSERVER_INCLUDE_LOCALDB", false);
                        if (!showLocalDb && instance.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        result.Add(new DatabaseSource(
                            Alias: alias,
                            ProviderType: DatabaseProviderType.SqlServer,
                            ConnectionString: new SqlConnectionStringBuilder
                            {
                                DataSource = instance,
                                InitialCatalog = dbName,
                                IntegratedSecurity = true,
                                ConnectTimeout = 3,
                                Encrypt = false,
                                TrustServerCertificate = true
                            }.ToString(),
                            DisplayName: $"SQL Server {instance} ({dbName})",
                            DefaultSchema: "dbo",
                            ReadOnly: true));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[database.registry.discovery] Could not query SQL Server instance '{instance}': {ex.Message}");
                }
            }
        }
        catch
        {
            // overall discovery failure leads to empty list
        }

        return result;
    }

    private static IEnumerable<DatabaseSource> LoadFromJsonFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var definition = JsonSerializer.Deserialize<DatabaseRegistryDefinition>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return ConvertDefinitions(definition?.Databases);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[database.registry.error] Failed to load database config from '{path}': {ex.Message}");
            return Array.Empty<DatabaseSource>();
        }
    }

    private static IEnumerable<DatabaseSource> LoadFromJsonString(string json)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<DatabaseRegistryDefinition>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return ConvertDefinitions(definition?.Databases);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[database.registry.error] Failed to parse DATABASE_CONFIG_JSON: {ex.Message}");
            return Array.Empty<DatabaseSource>();
        }
    }

    private static IEnumerable<DatabaseSource> ConvertDefinitions(IEnumerable<DatabaseSourceDefinition>? definitions)
    {
        if (definitions is null)
        {
            return Array.Empty<DatabaseSource>();
        }

        var result = new List<DatabaseSource>();

        foreach (var item in definitions)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Alias) || string.IsNullOrWhiteSpace(item.Provider) || string.IsNullOrWhiteSpace(item.ConnectionString))
            {
                continue;
            }

            var provider = ParseProvider(item.Provider);
            if (provider == DatabaseProviderType.Unknown)
            {
                continue;
            }

            result.Add(new DatabaseSource(
                Alias: item.Alias.Trim(),
                ProviderType: provider,
                ConnectionString: item.ConnectionString.Trim(),
                DisplayName: item.DisplayName?.Trim() ?? item.Alias.Trim(),
                DefaultSchema: string.IsNullOrWhiteSpace(item.DefaultSchema) ? null : item.DefaultSchema.Trim(),
                ReadOnly: item.ReadOnly ?? true));
        }

        return result;
    }

    private static DatabaseProviderType ParseProvider(string providerValue)
    {
        if (string.IsNullOrWhiteSpace(providerValue))
        {
            return DatabaseProviderType.Unknown;
        }

        return providerValue.Trim().ToLowerInvariant() switch
        {
            "sqlite" => DatabaseProviderType.SQLite,
            "sqlserver" => DatabaseProviderType.SqlServer,
            _ => DatabaseProviderType.Unknown
        };
    }

    public bool TryGetSource(string alias, out DatabaseSource source)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            source = default!;
            return false;
        }

        return _sources.TryGetValue(alias.Trim(), out source!);
    }

    public string GetSetupStatusText()
    {
        if (!_sources.Any())
        {
            return "No database sources configured. Set DATABASE_CONFIG_PATH or DATABASE_CONFIG_JSON, or VOICE_ADMIN_DB_PATH/VOICE_LAUNCHER_DB_PATH for Voice Admin.";
        }

        var names = string.Join(", ", _sources.Values.Select(s => s.Alias));
        return $"Database registry configured with {_sources.Count} source(s): {names}.";
    }
}
