using System.Text.Json.Serialization;

internal sealed class TelegramApiResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public T? Result { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

internal sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; init; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; init; }
}

internal sealed class TelegramMessage
{
    [JsonPropertyName("chat")]
    public TelegramChat Chat { get; init; } = new();

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("caption")]
    public string? Caption { get; init; }

    [JsonPropertyName("document")]
    public TelegramDocument? Document { get; init; }

    [JsonPropertyName("photo")]
    public List<TelegramPhotoSize>? Photo { get; init; }
}

internal sealed class TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}

internal sealed class TelegramDocument
{
    [JsonPropertyName("file_id")]
    public string FileId { get; init; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string? FileName { get; init; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; init; }
}

internal sealed class TelegramPhotoSize
{
    [JsonPropertyName("file_id")]
    public string FileId { get; init; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; init; }
}

internal sealed class TelegramRemoteFile
{
    [JsonPropertyName("file_id")]
    public string FileId { get; init; } = string.Empty;

    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; init; }
}
