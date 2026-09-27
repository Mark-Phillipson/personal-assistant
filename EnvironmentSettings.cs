using System.Text.RegularExpressions;

internal static class EnvironmentSettings
{
    public static void LoadDotEnvIfPresent()
    {
        foreach (var candidate in GetDotEnvCandidatePaths())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (TryLoadDotEnvFile(candidate))
            {
                break;
            }
        }
    }

    private static bool TryLoadDotEnvFile(string path)
    {
        var sawValidAssignment = false;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = line[(equalsIndex + 1)..].Trim();
            if (LooksLikeMalformedConcatenatedAssignment(value))
            {
                continue;
            }

            value = TrimQuotes(value);

            sawValidAssignment = true;
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        return sawValidAssignment;
    }

    private static bool LooksLikeMalformedConcatenatedAssignment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith('"') || value.StartsWith('\''))
        {
            return false;
        }

        return Regex.IsMatch(value, "^(?:true|false|[0-9]+)[A-Z_][A-Z0-9_]*=");
    }

    private static IEnumerable<string> GetDotEnvCandidatePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in EnumerateCandidateDirectories())
        {
            var candidate = Path.Combine(directory, ".env");
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateDirectories()
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (!ShouldSkipGeneratedDirectory(current))
            {
                yield return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && !ShouldSkipGeneratedDirectory(userProfile))
        {
            yield return userProfile;
        }
    }

    private static bool ShouldSkipGeneratedDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var segments = directory.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "publish", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "out", StringComparison.OrdinalIgnoreCase));
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
