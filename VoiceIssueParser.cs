using System.Linq;
using System.Text.RegularExpressions;

internal static class VoiceIssueParser
{
    public static (string Title, string? Body) ExtractTitleBody(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return (string.Empty, null);

        // Look for explicit title/description markers first
        var titleRegex = new Regex(@"(?:with the following title|title:|title is|titled)\s*(?<t>.+?)(?:\band the following description\b|\bwith the following description\b|\bdescription:\b|\bthe following description\b|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var descRegex = new Regex(@"(?:and the following description|with the following description|description:|the following description)\s*(?<d>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var titleMatch = titleRegex.Match(normalized);
        var descMatch = descRegex.Match(normalized);

        if (titleMatch.Success)
        {
            var title = titleMatch.Groups["t"].Value.Trim().TrimEnd('.', ',', ';');
            string? body = null;
            if (descMatch.Success)
            {
                body = descMatch.Groups["d"].Value.Trim();
            }
            else
            {
                // Try to use remaining text after the title marker as body
                var afterTitleIndex = titleMatch.Index + titleMatch.Length;
                if (afterTitleIndex < normalized.Length)
                {
                    var tail = normalized.Substring(afterTitleIndex).Trim();
                    if (!string.IsNullOrWhiteSpace(tail))
                        body = tail;
                }
            }

            return (CompressTitle(title), string.IsNullOrWhiteSpace(body) ? null : body);
        }

        if (descMatch.Success)
        {
            var body = descMatch.Groups["d"].Value.Trim();
            var possibleTitle = ExtractShortFragmentBeforeMarker(normalized, descMatch.Index);
            if (string.IsNullOrWhiteSpace(possibleTitle))
                return (CompressTitle(TakeWords(normalized, 6)), string.IsNullOrWhiteSpace(body) ? null : body);
            return (CompressTitle(possibleTitle), string.IsNullOrWhiteSpace(body) ? null : body);
        }

        // No explicit markers: split into sentence-like fragments
        var sentences = Regex.Split(normalized, @"(?<=[\.\?!])\s+").Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (sentences.Length == 0)
            return (CompressTitle(TakeWords(normalized, 6)), null);

        var stopWords = new[] { "create", "add", "issue", "todo", "please", "would", "could", "should", "need", "want" };
        foreach (var s in sentences)
        {
            var sl = s.ToLowerInvariant();
            if (s.Length <= 60 && !stopWords.Any(sw => sl.Contains(sw)))
            {
                var body = string.Join(" ", sentences.Where(x => x != s));
                return (CompressTitle(s.TrimEnd('.', '!', '?')), string.IsNullOrWhiteSpace(body) ? null : body);
            }
        }

        // Fallbacks
        var first = sentences[0].TrimEnd('.', '!', '?');
        var rest = string.Join(" ", sentences.Skip(1)).Trim();
        if (first.Length <= 80)
            return (CompressTitle(first), string.IsNullOrWhiteSpace(rest) ? null : rest);

        return (CompressTitle(TakeWords(normalized, 10)), null);
    }

    private static string ExtractShortFragmentBeforeMarker(string text, int markerIndex)
    {
        if (markerIndex <= 0) return string.Empty;
        var before = text.Substring(0, markerIndex).Trim();
            var parts = Regex.Split(before, @"[\.!\?]\s+").Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (parts.Length == 0) return string.Empty;
        var last = parts.Last();
        if (last.Length <= 80) return last;
        return TakeWords(last, 10);
    }

    private static string TakeWords(string s, int count)
    {
        var words = s.Split(new[] { ' ', '\t', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Take(count));
    }

    private static string CompressTitle(string t)
    {
        var t2 = Regex.Replace(t, @"\s+", " ").Trim();
        if (t2.Length > 80) t2 = t2.Substring(0, 80).Trim();
        return t2;
    }

}
