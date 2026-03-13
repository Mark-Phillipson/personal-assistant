using System.Text.Json;

internal enum AssistantTone
{
    Friendly,
    Professional,
    Witty,
    Calm,
    Irreverent
}

internal enum EmojiDensity
{
    Subtle,
    Moderate,
    Expressive
}

internal sealed record PersonalityProfile(
    string Name,
    AssistantTone Tone,
    bool UseEmoji,
    EmojiDensity EmojiDensity,
    string? SignatureGreeting,
    string? SignatureFarewell)
{
    public static PersonalityProfile FromEnvironment()
    {
        return new PersonalityProfile(
            Name: EnvironmentSettings.ReadString("ASSISTANT_NAME", "Bob"),
            Tone: ParseEnum<AssistantTone>(EnvironmentSettings.ReadString("ASSISTANT_TONE", AssistantTone.Friendly.ToString()), "ASSISTANT_TONE"),
            UseEmoji: EnvironmentSettings.ReadBool("ASSISTANT_USE_EMOJI", fallback: true),
            EmojiDensity: ParseEnum<EmojiDensity>(EnvironmentSettings.ReadString("ASSISTANT_EMOJI_DENSITY", EmojiDensity.Moderate.ToString()), "ASSISTANT_EMOJI_DENSITY"),
            SignatureGreeting: EnvironmentSettings.ReadOptionalString("ASSISTANT_SIGNATURE_GREETING"),
            SignatureFarewell: EnvironmentSettings.ReadOptionalString("ASSISTANT_SIGNATURE_FAREWELL"));
    }

    public static PersonalityProfile LoadFromEnvironmentOrJson(PersonalityProfile environmentProfile)
    {
        var configuredPath = EnvironmentSettings.ReadOptionalString("ASSISTANT_PERSONALITY_CONFIG_PATH");
        var path = string.IsNullOrWhiteSpace(configuredPath) ? "personality.json" : configuredPath;
        if (!File.Exists(path))
        {
            return environmentProfile;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var overrides = JsonSerializer.Deserialize<PersonalityProfileOverrides>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (overrides is null)
            {
                return environmentProfile;
            }

            return environmentProfile with
            {
                Name = string.IsNullOrWhiteSpace(overrides.Name) ? environmentProfile.Name : overrides.Name.Trim(),
                Tone = ParseOptionalEnum(overrides.Tone, environmentProfile.Tone),
                UseEmoji = overrides.UseEmoji ?? environmentProfile.UseEmoji,
                EmojiDensity = ParseOptionalEnum(overrides.EmojiDensity, environmentProfile.EmojiDensity),
                SignatureGreeting = overrides.SignatureGreeting?.Trim() ?? environmentProfile.SignatureGreeting,
                SignatureFarewell = overrides.SignatureFarewell?.Trim() ?? environmentProfile.SignatureFarewell
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[personality.config.error] Failed to read personality config at '{path}': {ex.Message}");
            return environmentProfile;
        }
    }

    private static TEnum ParseEnum<TEnum>(string raw, string settingName)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Environment variable '{settingName}' has invalid value '{raw}'.");
    }

    private static TEnum ParseOptionalEnum<TEnum>(string? raw, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return Enum.TryParse<TEnum>(raw.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private sealed class PersonalityProfileOverrides
    {
        public string? Name { get; init; }
        public string? Tone { get; init; }
        public bool? UseEmoji { get; init; }
        public string? EmojiDensity { get; init; }
        public string? SignatureGreeting { get; init; }
        public string? SignatureFarewell { get; init; }
    }
}
