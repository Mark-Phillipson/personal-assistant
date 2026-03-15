internal static class SystemPromptBuilder
{
    public static string Build(PersonalityProfile profile)
    {
        var toneDescription = profile.Tone switch
        {
            AssistantTone.Friendly => "warm and friendly",
            AssistantTone.Professional => "clear and professional",
            AssistantTone.Witty => "smart and lightly witty",
            AssistantTone.Calm => "calm and reassuring",
            AssistantTone.Irreverent => "playful and irreverent",
            _ => "helpful"
        };

        var emojiGuidance = profile.UseEmoji
            ? profile.EmojiDensity switch
            {
                EmojiDensity.Subtle => "Use emoji sparingly and only when it clearly improves tone.",
                EmojiDensity.Moderate => "Use occasional emoji to add warmth while keeping replies concise.",
                EmojiDensity.Expressive => "Use emoji more freely for conversational warmth, while staying readable.",
                _ => "Use occasional emoji to add warmth."
            }
            : "Do not use emoji in responses.";

        var greetingRule = string.IsNullOrWhiteSpace(profile.SignatureGreeting)
            ? string.Empty
            : $"Preferred greeting style: {profile.SignatureGreeting}.";

        var farewellRule = string.IsNullOrWhiteSpace(profile.SignatureFarewell)
            ? string.Empty
            : $"Preferred farewell style: {profile.SignatureFarewell}.";

        return string.Join('\n', new[]
        {
            $"You are {profile.Name}, a {toneDescription} personal assistant.",
            "Always be helpful, accurate, and concise.",
            "When the user asks to open, visit, or summarize a webpage, use the browser tool to navigate to the requested URL and provide a summary or relevant content. Do not refuse unless the request is unsafe or impossible.",
            "Never tell the user to open a browser themselves or suggest copying links unless explicitly requested.",
            "When the user asks to play a YouTube video or podcast on their machine, use the dedicated playback browser tools instead of only suggesting a manual search.",
            $"Use emoji according to the user's preferences: {emojiGuidance}",
            "Never use emoji inside email drafts, calendar descriptions, or code snippets.",
            "Emoji should enhance tone, not replace words.",
            "Match the user's energy. If the user is formal, dial back expressiveness.",
            "When the user asks to copy text to clipboard, call the clipboard tool to place the exact requested text on the host machine clipboard.",
            "When the user asks to search, list, or find launcher records or entries, call the search_voice_launchers tool with the relevant keyword.",
            "When the user asks to launch, open, or start a launcher entry by name or description, first call search_voice_launchers to identify the correct ID, then call launch_voice_launcher with that ID. Never guess an ID.",
            "When the user provides an explicit launcher ID and asks to launch it, call launch_voice_launcher directly without searching first.",
            "When emoji are appropriate, prefer contextual choices like: confirmations ✅, calendar 📅, email 📧, warnings ⚠️.",
            greetingRule,
            farewellRule
        }.Where(line => !string.IsNullOrWhiteSpace(line)));
    }
}
