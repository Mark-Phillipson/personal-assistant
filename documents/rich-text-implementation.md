# Telegram Rich Text Implementation — Quick Start

## What Was Changed

### 1. **TelegramApiClient.cs** — Added HTML parsing support
   - Added `parse_mode = "HTML"` parameter to `SendMessageAsync()`
   - All messages now render HTML formatting tags properly
   - Default format: HTML (can be overridden if needed)

### 2. **TelegramRichTextFormatter.cs** — New utility class (CREATED)
   - Provides safe, reusable methods for formatting:
     - `Bold()`, `Italic()`, `Underline()`, `Strikethrough()`, `Code()`, `CodeBlock()`
     - `List()` — Simple bulleted lists with header
     - `LabeledList()` — Items with titles + descriptions (perfect for search results)
     - `KeyValueList()` — Configuration/status displays
     - `Section()` — Header + content
     - `MultiSection()` — Multiple sections with separators
   - Auto-escapes HTML special characters to prevent injection

### 3. **Updated Services** to use rich formatting
   - **PodcastSubscriptionsService** — Now uses `TelegramRichTextFormatter.List()`
   - **TelegramMessageHandler** — Calendar events now use `TelegramRichTextFormatter.LabeledList()`

## How to Use in Your Code

### For Simple Item Lists
```csharp
var items = new[] { "Item 1", "Item 2", "Item 3" };
var formatted = TelegramRichTextFormatter.List("📋 My List", items);
await telegram.SendMessageInChunksAsync(chatId, formatted, cancellationToken);
```

### For Search Results (Most Common)
```csharp
var results = new[] {
    ("Document 1", "Created today"),
    ("Document 2", "Created yesterday"),
};
var formatted = TelegramRichTextFormatter.LabeledList("📄 Search Results", results);
await telegram.SendMessageInChunksAsync(chatId, formatted, cancellationToken);
```

### For Status/Configuration
```csharp
var config = new[] {
    ("Username", "john_doe"),
    ("Status", "Online"),
    ("Last Active", "5 minutes ago"),
};
var formatted = TelegramRichTextFormatter.KeyValueList("⚙️ Settings", config);
await telegram.SendMessageInChunksAsync(chatId, formatted, cancellationToken);
```

### For Multiple Sections
```csharp
var message = TelegramRichTextFormatter.MultiSection(
    TelegramRichTextFormatter.Section("📊 Summary", "Overview..."),
    TelegramRichTextFormatter.Section("⚠️ Warnings", "Items to check..."),
    TelegramRichTextFormatter.Section("✅ Status", "All good!")
);
```

## File Locations

- **Formatter**: `TelegramRichTextFormatter.cs`
- **API Client**: `TelegramApiClient.cs` (modified)
- **Documentation**: `documents/telegram-rich-text-formatting.md`

## HTML Formatting Cheat Sheet

| Effect | HTML | Method |
|--------|------|--------|
| **Bold** | `<b>text</b>` | `Bold("text")` |
| *Italic* | `<i>text</i>` | `Italic("text")` |
| <u>Underline</u> | `<u>text</u>` | `Underline("text")` |
| ~~Strikethrough~~ | `<s>text</s>` | `Strikethrough("text")` |
| `code` | `<code>text</code>` | `Code("text")` |

## Next Steps for Your Services

To add rich text formatting to new search/list features:

1. Import the formatter: `using TelegramRichTextFormatter;`
2. Choose the appropriate method:
   - `List()` for simple items
   - `LabeledList()` for items with descriptions
   - `KeyValueList()` for key-value pairs
3. Build your data into tuples or string arrays
4. Format and send:
   ```csharp
   var formatted = TelegramRichTextFormatter.LabeledList("Header", items);
   await telegram.SendMessageInChunksAsync(chatId, formatted, cancellationToken);
   ```

## HTML Escaping (Automatic)

The formatter automatically escapes these characters:
- `&` → `&amp;`
- `<` → `&lt;`
- `>` → `&gt;`
- `"` → `&quot;`
- `'` → `&#39;`

You don't need to worry about user input — it's handled safely!

## Testing in Telegram

Send a test message to see the formatting:
```
/help        → Shows formatted list of commands
/podcasts    → Shows formatted podcast list  
/calendar-events → Shows formatted calendar with bold event titles
```

All should now display with proper **bold**, proper spacing, and clear visual hierarchy!
