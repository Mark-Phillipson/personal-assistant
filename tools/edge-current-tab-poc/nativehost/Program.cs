using System.Text;
using System.Text.Json;

const string HostName = "com.personalassistant.edgetab";

var stdout = Console.OpenStandardOutput();
var stdin = Console.OpenStandardInput();

while (true)
{
    try
    {
        var message = ReadNativeMessage(stdin);
        if (string.IsNullOrWhiteSpace(message))
        {
            continue;
        }

        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;

        var action = root.TryGetProperty("action", out var actionElement)
            ? actionElement.GetString() ?? "unknown"
            : "unknown";

        var response = new
        {
            success = true,
            host = HostName,
            action,
            timestampUtc = DateTimeOffset.UtcNow,
            message = "Native host received a request.",
            payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement : JsonDocument.Parse("{}" ).RootElement
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false });
        WriteNativeMessage(stdout, json);
    }
    catch (Exception ex)
    {
        var error = new
        {
            success = false,
            error = ex.Message,
            timestampUtc = DateTimeOffset.UtcNow
        };

        WriteNativeMessage(stdout, JsonSerializer.Serialize(error));
    }
}

static string ReadNativeMessage(Stream input)
{
    var lengthBuffer = new byte[4];
    var read = 0;

    while (read < 4)
    {
        var count = input.Read(lengthBuffer, read, 4 - read);
        if (count <= 0)
        {
            return string.Empty;
        }

        read += count;
    }

    var length = BitConverter.ToInt32(lengthBuffer, 0);
    if (length <= 0 || length > 1024 * 1024)
    {
        return string.Empty;
    }

    var buffer = new byte[length];
    read = 0;

    while (read < length)
    {
        var count = input.Read(buffer, read, length - read);
        if (count <= 0)
        {
            return string.Empty;
        }

        read += count;
    }

    return Encoding.UTF8.GetString(buffer);
}

static void WriteNativeMessage(Stream output, string json)
{
    var payload = Encoding.UTF8.GetBytes(json);
    var length = BitConverter.GetBytes(payload.Length);
    output.Write(length, 0, length.Length);
    output.Write(payload, 0, payload.Length);
    output.Flush();
}
