# Telegram Rich Text Formatting Guide

This guide explains how to use the `TelegramRichTextFormatter` utility to format messages with bold, italic, lists, and other rich text styles in Telegram.

## Overview

The Telegram Bot API supports HTML formatting for messages. The `TelegramRichTextFormatter` utility provides helper methods to make it easy to create well-formatted, readable messages.

## Basic Formatting

### Bold Text
```csharp
var boldText = TelegramRichTextFormatter.Bold("Important Information");
// Output in Telegram: **Important Information** (displayed as bold)
```

### Italic Text
```csharp
var italicText = TelegramRichTextFormatter.Italic("Note: This is important");
// Output in Telegram: *Note: This is important* (displayed as italic)
```

### Underline Text
```csharp
var underlined = TelegramRichTextFormatter.Underline("Underlined Text");
```

### Strikethrough Text
```csharp
var strikethrough = TelegramRichTextFormatter.Strikethrough("Old Information");
```

### Code/Monospace
```csharp
var code = TelegramRichTextFormatter.Code("variable_name");
var codeBlock = TelegramRichTextFormatter.CodeBlock("public void MyMethod() { }");
```

## Structured Lists

### Simple List
Perfect for displaying items with a bold header:

```csharp
var podcastNames = new[] { "The Daily", "Mindful Morning", "Tech Talk Weekly" };
var result = TelegramRichTextFormatter.List("🎵 Subscribed Podcasts", podcastNames);

// Output:
// **🎵 Subscribed Podcasts**
// • The Daily
// • Mindful Morning
// • Tech Talk Weekly
```

### Labeled Lists (Item + Description)
Great for search results, documents, or any items with secondary information:

```csharp
var items = new[]
{
    ("report.pdf", "Updated today at 2:30 PM"),
    ("notes.txt", "Created yesterday"),
    ("meeting-notes.docx", "Added 3 days ago")
};

var result = TelegramRichTextFormatter.LabeledList("📄 Recent Documents", items);

// Output:
// **📄 Recent Documents**
// • **report.pdf**
//    Updated today at 2:30 PM
// • **notes.txt**
//    Created yesterday
// • **meeting-notes.docx**
//    Added 3 days ago
```

### Key-Value List
Perfect for configuration, status information, or structured data:

```csharp
var pairs = new[]
{
    ("Theme", "Dark Mode"),
    ("Language", "English"),
    ("Timezone", "UTC+0"),
    ("Auto-save", "Enabled")
};

var result = TelegramRichTextFormatter.KeyValueList("⚙️ Settings", pairs);

// Output:
// **⚙️ Settings**
// **Theme**: Dark Mode
// **Language**: English
// **Timezone**: UTC+0
// **Auto-save**: Enabled
```

## Advanced Formatting

### Sections with Headers
```csharp
var section = TelegramRichTextFormatter.Section("📝 Summary", "This is the main content...");

// Output:
// **📝 Summary**
// This is the main content...
```

### Multiple Sections with Separators
```csharp
var content = TelegramRichTextFormatter.MultiSection(
    TelegramRichTextFormatter.Section("📊 Overview", "Summary data..."),
    TelegramRichTextFormatter.Section("⚠️ Warnings", "Check these items..."),
    TelegramRichTextFormatter.Section("✅ Status", "Everything is good!")
);

// Output with separators between each section:
// **📊 Overview**
// Summary data...
// 
// ─────────────────
// 
// **⚠️ Warnings**
// Check these items...
// 
// ─────────────────
// 
// **✅ Status**
// Everything is good!
```

### Custom Separators
```csharp
var separator = TelegramRichTextFormatter.Separator("═════════════════");
var separator2 = TelegramRichTextFormatter.Separator("***");
```

## Real-World Examples

### Search Results
```csharp
public string FormatSearchResults(List<SearchResult> results)
{
    if (results.Count == 0)
        return "No results found.";
    
    var items = results.Select(r => 
        (r.Title, $"Score: {r.Score}% - {r.Brief}")
    ).ToList();
    
    return TelegramRichTextFormatter.LabeledList("🔍 Search Results", items);
}
```

### User Status Update
```csharp
var status = TelegramRichTextFormatter.KeyValueList("👤 User Status",
    new[]
    {
        ("Username", "john_doe"),
        ("Status", "Online"),
        ("Last Seen", "Just now"),
        ("Messages Today", "42")
    }
);
```

### Calendar Events
```csharp
var eventItems = events.Select(ev => (
    $"{TelegramRichTextFormatter.Bold(ev.Summary)}",
    $"Start: {ev.Start}\nEnd: {ev.End}"
)).ToList();

var result = TelegramRichTextFormatter.LabeledList("📅 Upcoming Events", eventItems);
```

## HTML Formatting Reference

The formatter uses HTML tags internally. If you need custom formatting not covered by the utility:

| Format | HTML Tag | Example |
|--------|----------|---------|
| Bold | `<b>text</b>` | `<b>Important</b>` |
| Italic | `<i>text</i>` | `<i>Note</i>` |
| Underline | `<u>text</u>` | `<u>Underlined</u>` |
| Strikethrough | `<s>text</s>` | `<s>Old</s>` |
| Monospace | `<code>text</code>` | `<code>variable</code>` |
| Code Block | `<pre>text</pre>` | `<pre>function()</pre>` |

## Important Notes

1. **HTML Escaping**: The formatter automatically escapes HTML special characters (`&`, `<`, `>`, `"`, `'`) to prevent injection and rendering issues.

2. **Parse Mode**: Messages sent through `TelegramApiClient.SendMessageInChunksAsync()` now automatically use HTML parse mode, so all formatted output will render correctly.

3. **Line Breaks**: Use `\n` for line breaks in formatted text.

4. **Combining Styles**: You can combine method calls:
   ```csharp
   var bold = TelegramRichTextFormatter.Bold(
       TelegramRichTextFormatter.Underline("Bold and Underlined")
   );
   // This will create: <b><u>text</u></b>
   ```

5. **Maximum Message Length**: Telegram has a 4096-character limit per message. The API client automatically chunks long messages, but be mindful of this when building large formatted outputs.

## When to Use Each Format

| Method | Best For |
|--------|----------|
| `List()` | Simple item lists, especially with headers |
| `LabeledList()` | Search results, file listings, any items with descriptions |
| `KeyValueList()` | Configuration, status displays, structured data |
| `Section()` | Individual topic heading + content |
| `MultiSection()` | Multiple distinct topics with clear separation |
| `Bold()` | Highlighting important words within text |
| `Italic()` | Emphasis, notes, or additional context |
| `Code()` | Code snippets, variable names, commands |

## Creating New List Services

When adding new services that return search/list results, follow this pattern:

```csharp
public string GetFormattedResults(List<Item> items)
{
    if (items.Count == 0)
        return "No items found.";
    
    var formatted = items.Select(item => 
        (item.Name, $"Description: {item.Description}")
    );
    
    return TelegramRichTextFormatter.LabeledList("🔍 Search Results", formatted);
}
```

This ensures consistent, readable formatting across all search and list features in the assistant.
