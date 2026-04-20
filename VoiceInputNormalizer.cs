using System.Text.RegularExpressions;

internal static class VoiceInputNormalizer
{
    public static string NormalizeTranscript(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw;

        // Replace ellipses and similar artifacts with a space
        s = s.Replace("...", " ").Replace("…", " ");
        // Remove sequences of unicode ellipses or dots that may appear attached to words
        s = Regex.Replace(s, @"\u2026+", " ");

        // Remove sequences of dots that may appear attached to words
        s = Regex.Replace(s, @"\.+", " ");

        // Remove some common filler tokens (simple, non-exhaustive)
        var fillers = new[] { " um ", " uh ", " mm ", " like ", " you know ", " i mean " };
        foreach (var f in fillers)
        {
            s = s.Replace(f, " ", System.StringComparison.OrdinalIgnoreCase);
        }

        // Fix spacing around punctuation: no space before punctuation, single space after
        s = Regex.Replace(s, @"\s+([,.;:!?])", "$1");
        s = Regex.Replace(s, @"([,.;:!?])(?!\s)", "$1 ");

        // Collapse whitespace
        s = Regex.Replace(s, @"\s+", " ").Trim();

        return s;
    }
}
