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

internal sealed record PersonalityProfile
{
    public string Name { get; init; } = "Bob";
    public AssistantTone Tone { get; init; } = AssistantTone.Friendly;
    public bool UseEmoji { get; init; } = true;
    public EmojiDensity EmojiDensity { get; init; } = EmojiDensity.Moderate;
    public string? SignatureGreeting { get; init; }
    public string? SignatureFarewell { get; init; }
    public IReadOnlyList<string> SignatureGreetings { get; init; } = [];
    public IReadOnlyList<string> SignatureFarewells { get; init; } = [];

    public static PersonalityProfile FromEnvironment()
    {
        var signatureGreeting = EnvironmentSettings.ReadOptionalString("ASSISTANT_SIGNATURE_GREETING");
        var signatureFarewell = EnvironmentSettings.ReadOptionalString("ASSISTANT_SIGNATURE_FAREWELL");

        return new PersonalityProfile
        {
            Name = EnvironmentSettings.ReadString("ASSISTANT_NAME", "Bob"),
            Tone = ParseEnum<AssistantTone>(EnvironmentSettings.ReadString("ASSISTANT_TONE", AssistantTone.Friendly.ToString()), "ASSISTANT_TONE"),
            UseEmoji = EnvironmentSettings.ReadBool("ASSISTANT_USE_EMOJI", fallback: true),
            EmojiDensity = ParseEnum<EmojiDensity>(EnvironmentSettings.ReadString("ASSISTANT_EMOJI_DENSITY", EmojiDensity.Moderate.ToString()), "ASSISTANT_EMOJI_DENSITY"),
            SignatureGreeting = signatureGreeting,
            SignatureFarewell = signatureFarewell,
            SignatureGreetings = NormalizeSignatureOptions(null, signatureGreeting),
            SignatureFarewells = NormalizeSignatureOptions(null, signatureFarewell)
        };
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

            var signatureGreetings = ResolveSignatureOptions(
                overrides.SignatureGreetings,
                overrides.SignatureGreeting,
                environmentProfile.SignatureGreetings,
                environmentProfile.SignatureGreeting);

            var signatureFarewells = ResolveSignatureOptions(
                overrides.SignatureFarewells,
                overrides.SignatureFarewell,
                environmentProfile.SignatureFarewells,
                environmentProfile.SignatureFarewell);

            return environmentProfile with
            {
                Name = string.IsNullOrWhiteSpace(overrides.Name) ? environmentProfile.Name : overrides.Name.Trim(),
                Tone = ParseOptionalEnum(overrides.Tone, environmentProfile.Tone),
                UseEmoji = overrides.UseEmoji ?? environmentProfile.UseEmoji,
                EmojiDensity = ParseOptionalEnum(overrides.EmojiDensity, environmentProfile.EmojiDensity),
                SignatureGreeting = signatureGreetings.FirstOrDefault(),
                SignatureFarewell = signatureFarewells.FirstOrDefault(),
                SignatureGreetings = signatureGreetings,
                SignatureFarewells = signatureFarewells
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[personality.config.error] Failed to read personality config at '{path}': {ex.Message}");
            return environmentProfile;
        }
    }

    public string? GetRandomSignatureGreeting() => GetRandomOption(SignatureGreetings);

    public string? GetRandomSignatureFarewell() => GetRandomOption(SignatureFarewells);

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

    private static IReadOnlyList<string> ResolveSignatureOptions(
        IReadOnlyList<string>? overrides,
        string? legacyOverride,
        IReadOnlyList<string> environmentValues,
        string? environmentLegacyValue)
    {
        var merged = NormalizeSignatureOptions(overrides, legacyOverride);
        return merged.Count > 0
            ? merged
            : NormalizeSignatureOptions(environmentValues, environmentLegacyValue);
    }

    private static IReadOnlyList<string> NormalizeSignatureOptions(IEnumerable<string>? options, string? legacyValue)
    {
        var normalized = (options ?? [])
            .Append(legacyValue)
            .Select(static value => value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return normalized;
    }

    private static string? GetRandomOption(IReadOnlyList<string> options)
    {
        if (options.Count == 0)
        {
            return null;
        }

        return options[Random.Shared.Next(options.Count)];
    }

    private sealed class PersonalityProfileOverrides
    {
        public string? Name { get; init; }
        public string? Tone { get; init; }
        public bool? UseEmoji { get; init; }
        public string? EmojiDensity { get; init; }
        public string? SignatureGreeting { get; init; }
        public string? SignatureFarewell { get; init; }
        public string[]? SignatureGreetings { get; init; }
        public string[]? SignatureFarewells { get; init; }
    }
}
