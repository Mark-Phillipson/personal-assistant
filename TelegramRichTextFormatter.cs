/// <summary>
/// Utility for formatting rich text messages for Telegram using HTML parse mode.
/// Provides helpers for bold, italic, lists, and structured result displays.
/// </summary>
internal static class TelegramRichTextFormatter
{
    /// <summary>
    /// Wraps text in bold HTML tags.
    /// </summary>
    public static string Bold(string text) => $"<b>{EscapeHtml(text)}</b>";

    /// <summary>
    /// Wraps text in italic HTML tags.
    /// </summary>
    public static string Italic(string text) => $"<i>{EscapeHtml(text)}</i>";

    /// <summary>
    /// Wraps text in underline HTML tags.
    /// </summary>
    public static string Underline(string text) => $"<u>{EscapeHtml(text)}</u>";

    /// <summary>
    /// Wraps text in strikethrough HTML tags.
    /// </summary>
    public static string Strikethrough(string text) => $"<s>{EscapeHtml(text)}</s>";

    /// <summary>
    /// Wraps text in code tags (monospace).
    /// </summary>
    public static string Code(string text) => $"<code>{EscapeHtml(text)}</code>";

    /// <summary>
    /// Wraps text in pre tags (monospace block).
    /// </summary>
    public static string CodeBlock(string text) => $"<pre>{EscapeHtml(text)}</pre>";

    /// <summary>
    /// Creates a formatted list with bold header and bullet items.
    /// Items are separated by newlines for easy readability.
    /// </summary>
    /// <example>
    /// var list = TelegramRichTextFormatter.List(
    ///     "Search Results",
    ///     new[] { "Item 1", "Item 2", "Item 3" }
    /// );
    /// </example>
    public static string List(string header, IEnumerable<string> items)
    {
        var lines = new List<string> { Bold(header) };
        lines.AddRange(items.Select(item => $"• {EscapeHtml(item)}"));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Creates a formatted list where each item can have a bold label and secondary text.
    /// Useful for search results with title and description.
    /// </summary>
    /// <example>
    /// var results = TelegramRichTextFormatter.LabeledList(
    ///     "Documents",
    ///     new[] {
    ///         ("report.pdf", "Added today"),
    ///         ("notes.txt", "Updated yesterday")
    ///     }
    /// );
    /// </example>
    public static string LabeledList(string header, IEnumerable<(string label, string secondary)> items)
    {
        var lines = new List<string> { Bold(header) };
        
        foreach (var (label, secondary) in items)
        {
            var line = $"• {Bold(EscapeHtml(label))}";
            if (!string.IsNullOrWhiteSpace(secondary))
            {
                line += $"\n   {EscapeHtml(secondary)}";
            }
            lines.Add(line);
        }
        
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Creates a two-column formatted list for key-value pairs.
    /// Useful for displaying settings, configuration, or structured data.
    /// </summary>
    /// <example>
    /// var config = TelegramRichTextFormatter.KeyValueList(
    ///     "Settings",
    ///     new[] {
    ///         ("Theme", "Dark"),
    ///         ("Language", "English"),
    ///         ("Timezone", "UTC+0")
    ///     }
    /// );
    /// </example>
    public static string KeyValueList(string header, IEnumerable<(string key, string value)> pairs)
    {
        var lines = new List<string> { Bold(header) };
        
        foreach (var (key, value) in pairs)
        {
            lines.Add($"{Bold(EscapeHtml(key))}: {EscapeHtml(value)}");
        }
        
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Creates a section with header and content, both on separate lines.
    /// </summary>
    public static string Section(string header, string content)
    {
        return $"{Bold(header)}\n{EscapeHtml(content)}";
    }

    /// <summary>
    /// Creates a divider separator line using an emoji or text.
    /// </summary>
    public static string Separator(string? divider = null) => divider ?? "─────────────────";

    /// <summary>
    /// Combines multiple formatted sections with separators between them.
    /// </summary>
    public static string MultiSection(params string[] sections)
    {
        return string.Join($"\n\n{Separator()}\n\n", sections);
    }

    /// <summary>
    /// Escapes HTML special characters to prevent injection and rendering issues.
    /// </summary>
    private static string EscapeHtml(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }
}
