using System.Net;
using System.Text;
using System.Text.Json;

internal static class CommandApiServer
{
    public static async Task StartAsync(CancellationToken cancellationToken)
    {
        var prefix = EnvironmentSettings.ReadString("ANDROID_COMPANION_API_PREFIX", "http://localhost:5000/");
        var expectedDeviceToken = EnvironmentSettings.ReadOptionalString("ANDROID_DEVICE_TOKEN");

        if (!prefix.EndsWith("/"))
        {
            prefix += "/";
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"[command-api] Failed to start HttpListener on {prefix}: {ex.Message}");
            return;
        }

        Console.WriteLine($"[command-api] Listening at {prefix} (token required: { !string.IsNullOrWhiteSpace(expectedDeviceToken) })");

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[command-api] Accept error: {ex.Message}");
                continue;
            }

            _ = Task.Run(async () => await HandleRequestAsync(context, expectedDeviceToken), cancellationToken);
        }

        listener.Stop();
    }

    private static async Task HandleRequestAsync(HttpListenerContext context, string? expectedDeviceToken)
    {
        var request = context.Request;
        var response = context.Response;

        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.Url?.AbsolutePath, "/api/command", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.Close();
            return;
        }

        try
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            var commandRequest = JsonSerializer.Deserialize<CommandApiRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (commandRequest is null || string.IsNullOrWhiteSpace(commandRequest.Command))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(response, new CommandApiResponse("Invalid request: missing command.", null, false));
                return;
            }

            if (!string.IsNullOrWhiteSpace(expectedDeviceToken) && commandRequest.DeviceToken != expectedDeviceToken)
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await WriteJsonResponseAsync(response, new CommandApiResponse("Invalid device token.", null, false));
                return;
            }

            var lower = commandRequest.Command.Trim().ToLowerInvariant();
            var actions = new List<CommandAction>();
            var textResponse = $"Received command: {commandRequest.Command}";

            // Basic heuristic mapping for initial MVP
            if (lower.Contains("open youtube"))
            {
                actions.Add(new CommandAction("device.open_app", new Dictionary<string, string> { ["name"] = "youtube" }));
                textResponse = "Opening YouTube.";
            }
            else if (lower.Contains("open ") && (lower.Contains("http://") || lower.Contains("https://")))
            {
                var targetUrl = commandRequest.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
                actions.Add(new CommandAction("device.open_url", new Dictionary<string, string> { ["url"] = targetUrl }));
                textResponse = $"Opening URL {targetUrl}";
            }
            else if (lower.Contains("home") || lower.Contains("go home"))
            {
                actions.Add(new CommandAction("device.navigate", new Dictionary<string, string> { ["action"] = "home" }));
                textResponse = "Navigating home.";
            }
            else
            {
                textResponse = "Command received, but no action matched. Adjust prompt or implement additional mappings.";
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            await WriteJsonResponseAsync(response, new CommandApiResponse(textResponse, actions, true));
        }
        catch (Exception ex)
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            Console.Error.WriteLine($"[command-api] Request handler error: {ex}");
            await WriteJsonResponseAsync(response, new CommandApiResponse($"Internal server error: {ex.Message}", null, false));
        }
        finally
        {
            response.Close();
        }
    }

    private static async Task WriteJsonResponseAsync(HttpListenerResponse response, CommandApiResponse value)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
        var payload = JsonSerializer.Serialize(value, options);
        var bytes = Encoding.UTF8.GetBytes(payload);

        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private record CommandApiRequest(string Command, string DeviceToken);
    private record CommandAction(string Type, Dictionary<string, string>? Params);
    private record CommandApiResponse(string TextResponse, List<CommandAction>? Actions, bool Success);
}
