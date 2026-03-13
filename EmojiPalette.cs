internal static class EmojiPalette
{
    public const string Calendar = "📅";
    public const string Email = "📧";
    public const string Confirm = "✅";
    public const string Warning = "⚠️";
    public const string Thinking = "🤔";
    public const string Happy = "😊";
    public const string Wave = "👋";
    public const string Rocket = "🚀";
    public const string Search = "🔍";
    public const string Music = "🎵";
    public const string Lock = "🔒";
    public const string Personality = "🤖";
    public const string Commands = "📋";

    public static string Wrap(string text, string emoji, bool useEmoji)
        => useEmoji ? $"{emoji} {text}" : text;
}
