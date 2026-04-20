using System;
using System.Reflection;
using Xunit;

namespace PersonalAssistant.Tests
{
    public class TodoSelectionTests
    {
        [Fact]
        public void VoiceParserProducesShorterTitleThanLegacyForFragmentedTranscript()
        {
            var text = "Create another issue in the... Github Todos With the following title. Windows notifications. And the following description. We need to get to the... Windows notifications working in the /help";

            // Call legacy extractor via reflection
            var type = typeof(TelegramMessageHandler);
            var method = type.GetMethod("ExtractTodoDataFromText", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var legacyResult = method!.Invoke(null, new object[] { text });
            Assert.NotNull(legacyResult);

            var todoType = legacyResult.GetType();
            var titleProp = todoType.GetProperty("Title", BindingFlags.Public | BindingFlags.Instance);
            var descProp = todoType.GetProperty("Description", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(titleProp);

            var legacyTitle = (string?)titleProp!.GetValue(legacyResult);
            var legacyDesc = (string?)descProp!.GetValue(legacyResult);

            var normalized = VoiceInputNormalizer.NormalizeTranscript(text);
            var (proposedTitle, proposedBody) = VoiceIssueParser.ExtractTitleBody(normalized);

            Assert.False(string.IsNullOrWhiteSpace(proposedTitle));
            Assert.True(proposedTitle.Length <= (legacyTitle?.Length ?? int.MaxValue), "Proposed title should be shorter or equal to legacy title");
            Assert.False(string.IsNullOrWhiteSpace(proposedBody));
        }
    }
}
