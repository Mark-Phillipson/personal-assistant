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
}

internal sealed class TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}
