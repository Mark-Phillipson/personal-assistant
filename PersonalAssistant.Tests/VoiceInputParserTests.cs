using System;
using Xunit;

namespace PersonalAssistant.Tests
{
    public class VoiceInputParserTests
    {
        [Fact]
        public void Normalize_ReplacesEllipsesAndCollapsesSpaces()
        {
            var raw = "This is a test...   with  lots... um spaces";
            var norm = VoiceInputNormalizer.NormalizeTranscript(raw);
            Assert.DoesNotContain("...", norm);
            Assert.DoesNotContain("  ", norm);
            Assert.Contains("This is a test", norm, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExtractTitleBody_WithMarkers_ExtractsTitleAndBody()
        {
            var raw = "Create another issue in the... Github Todos With the following title. Windows notifications. And the following description. We need to get to the... Windows notifications working in the /help";
            var normalized = VoiceInputNormalizer.NormalizeTranscript(raw);
            var (title, body) = VoiceIssueParser.ExtractTitleBody(normalized);

            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.True(title.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0, $"Title was: '{title}'");
            Assert.False(string.IsNullOrWhiteSpace(body));
            Assert.Contains("Windows notifications", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
