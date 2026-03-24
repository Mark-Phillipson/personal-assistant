internal static class EnvironmentSettings
{
    public static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"Required environment variable '{name}' is missing.");
    }

    public static int ReadInt(string name, int fallback, int min, int max)
    {
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
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return TrimQuotes(raw.Trim());
    }

    public static string? ReadOptionalString(string name)
    {
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
