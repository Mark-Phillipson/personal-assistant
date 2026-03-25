using System;
using System.Text.RegularExpressions;

internal static class SqlSecurityHelper
{
    private static readonly Regex SelectOnlyPattern = new(
        "^(\\s*)(SELECT|WITH)\\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ForbiddenKeywordsPattern = new(
        "\\b(INSERT|UPDATE|DELETE|MERGE|ALTER|DROP|TRUNCATE|CREATE|EXEC|EXECUTE|USE|GRANT|REVOKE|BACKUP|RESTORE|DECLARE|SET|PRAGMA|ATTACH|DETACH|REINDEX)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSelectQueryOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var trimmed = sql.Trim();
        if (!SelectOnlyPattern.IsMatch(trimmed))
        {
            return false;
        }

        if (ForbiddenKeywordsPattern.IsMatch(trimmed))
        {
            return false;
        }

        // Prevent batching using ";"
        if (trimmed.IndexOf(';') >= 0 && !trimmed.EndsWith(";"))
        {
            return false;
        }

        // Allow final semicolon only
        trimmed = trimmed.TrimEnd();
        if (trimmed.EndsWith(";"))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return !string.IsNullOrWhiteSpace(trimmed);
    }
}
