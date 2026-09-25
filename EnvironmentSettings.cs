internal static class EnvironmentSettings
{
    public static void LoadDotEnvIfPresent()
    {
        var candidatePaths = GetDotEnvCandidatePaths();

        foreach (var candidate in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            foreach (var rawLine in File.ReadAllLines(candidate))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0)
                {
                    continue;
                }

                var key = line[..equalsIndex].Trim();
                var value = line[(equalsIndex + 1)..].Trim();
                value = TrimQuotes(value);

                if (!string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }

            break;
        }
    }

    private static IEnumerable<string> GetDotEnvCandidatePaths()
    {
        var workingDirectory = Directory.GetCurrentDirectory();
        var baseDirectory = AppContext.BaseDirectory;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var directories = new List<string>
        {
            workingDirectory,
            baseDirectory,
            userProfile
        };

        foreach (var directory in new[] { workingDirectory, baseDirectory })
        {
            var current = directory;
            while (!string.IsNullOrEmpty(current))
            {
                directories.Add(current);
                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }
                current = parent.FullName;
            }
        }

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(directory, ".env");
        }
    }

    public static string Require(string name)
    {
        LoadDotEnvIfPresent();
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"Required environment variable '{name}' is missing.");
    }

    public static int ReadInt(string name, int fallback, int min, int max)
    {
        LoadDotEnvIfPresent();
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, out var parsed) || parsed < min || parsed > max)
        {
            throw new InvalidOperationException(
                $"Environment variable '{name}' must be an integer between {min} and {max}.");
        }

        return parsed;
    }

    public static string ReadString(string name, string fallback)
    {
        LoadDotEnvIfPresent();
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return TrimQuotes(raw.Trim());
    }

    public static string? ReadOptionalString(string name)
    {
        LoadDotEnvIfPresent();
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = TrimQuotes(raw.Trim());
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string TrimQuotes(string value)
    {
        if (value.Length >= 2 && ((value.StartsWith("\"") && value.EndsWith("\"")) || (value.StartsWith("'") && value.EndsWith("'"))))
        {
            return value.Substring(1, value.Length - 2).Trim();
        }

        return value;
    }

    public static bool ReadBool(string name, bool fallback)
    {
        LoadDotEnvIfPresent();
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!bool.TryParse(raw.Trim(), out var parsed))
        {
            throw new InvalidOperationException($"Environment variable '{name}' must be true or false.");
        }

        return parsed;
    }
}
