using GitHub.Copilot.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

internal sealed class WhatsAppMessageSummarizer
{
    private const int MaxMessageBatchSize = 20;
    private const int MaxCharactersPerSummary = 1000;

    private readonly CopilotClient _copilotClient;
    private readonly string? _modelOverride;

    public WhatsAppMessageSummarizer(CopilotClient copilotClient, string? modelOverride = null)
    {
        _copilotClient = copilotClient;
        _modelOverride = modelOverride;
    }

    /// <summary>
    /// Summarize a list of WhatsApp messages into a single coherent summary.
    /// </summary>
    public async Task<string> SummarizeMessagesAsync(List<WhatsAppMessage> messages)
    {
        if (messages.Count == 0)
            return "No messages found.";

        // Group messages by date for better context
        var messagesByDate = messages
            .GroupBy(m => m.Timestamp.Date)
            .OrderByDescending(g => g.Key)
            .ToList();

        var summaries = new List<string>();

        foreach (var dayGroup in messagesByDate)
        {
            var daySummary = await SummarizeDayMessagesAsync(dayGroup.Key, dayGroup.ToList());
            summaries.Add(daySummary);
        }

        // Combine daily summaries into an overall summary
        if (summaries.Count == 1)
            return summaries[0];

        var combinedSummary = await CombineSummariesAsync(summaries);
        return combinedSummary;
    }

    private async Task<string> SummarizeDayMessagesAsync(DateTime date, List<WhatsAppMessage> messages)
    {
        var dateStr = date.ToString("MMMM dd, yyyy");
        var messageText = FormatMessagesForSummary(messages);

        var prompt = $@"Summarize the following WhatsApp group chat messages from {dateStr}. 
Keep the summary concise (2-3 sentences), highlighting key topics and decisions discussed.
Focus on important information, events, or announcements. Ignore trivial messages.

Messages:
{messageText}

Summary:";

        try
        {
            await using var session = await _copilotClient.CreateSessionAsync(new SessionConfig
                {
                    Model = _modelOverride
                });

            var response = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt });

            var summary = response?.Data.Content?.Trim() ?? "Unable to generate summary.";
            return $"**{dateStr}**: {summary}";
        }
        catch (Exception ex)
        {
            return $"**{dateStr}**: Error summarizing messages: {ex.Message}";
        }
    }

    private async Task<string> CombineSummariesAsync(List<string> summaries)
    {
        if (summaries.Count <= 1)
            return string.Join("\n\n", summaries);

        var combinedText = string.Join("\n\n", summaries);
        var prompt = $@"The following are daily summaries of WhatsApp group chat messages. 
Create a comprehensive overview highlighting the most important topics, decisions, and events across all days.
Keep it concise (200-300 words max).

Daily summaries:
{combinedText}

Overall summary:";

        try
        {
            await using var session = await _copilotClient.CreateSessionAsync(new SessionConfig
                {
                    Model = _modelOverride
                });

            var response = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt });

            var summary = response?.Data.Content?.Trim() ?? "Unable to generate overall summary.";
            return summary;
        }
        catch (Exception ex)
        {
            return $"Error creating overall summary: {ex.Message}";
        }
    }

    private string FormatMessagesForSummary(List<WhatsAppMessage> messages)
    {
        var sb = new StringBuilder();

        // Batch messages to avoid token overflow
        var batches = messages
            .Select((message, index) => new { message, index })
            .GroupBy(x => x.index / MaxMessageBatchSize)
            .Select(g => g.Select(x => x.message).ToList())
            .ToList();

        foreach (var batch in batches.Take(5)) // Limit to first 5 batches to stay within token limits
        {
            foreach (var msg in batch)
            {
                sb.AppendLine($"{msg.Sender} ({msg.Timestamp:HH:mm}): {msg.Body}");
            }
        }

        if (batches.Count > 5)
            sb.AppendLine($"... and {messages.Count - (5 * MaxMessageBatchSize)} more messages");

        return sb.ToString();
    }
}
