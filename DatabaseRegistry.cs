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

        return new DatabaseRegistry(sources);
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
