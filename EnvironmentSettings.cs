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
}
