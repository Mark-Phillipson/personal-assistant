using System.Net;
using System.Text;
using System.Text.Json;

internal static class CommandApiServer
{
    public static async Task StartAsync(
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<string>>? aiFallback = null)
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

            _ = Task.Run(async () => await HandleRequestAsync(context, expectedDeviceToken, aiFallback), cancellationToken);
        }

        listener.Stop();
    }

    private static async Task HandleRequestAsync(HttpListenerContext context, string? expectedDeviceToken, Func<string, CancellationToken, Task<string>>? aiFallback)
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

            Console.WriteLine($"[command-api] Command from '{commandRequest.DeviceName ?? "unknown"}': {commandRequest.Command}");

            // URL open: any command containing a http/https URL
            if (lower.Contains("http://") || lower.Contains("https://"))
            {
                var targetUrl = commandRequest.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(t => t.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                actions.Add(new CommandAction("device.open_url", new Dictionary<string, string> { ["url"] = targetUrl }));
                textResponse = $"Opening URL {targetUrl}.";
            }
            // Navigation: home / back
            else if (lower == "go home" || lower == "home" || lower == "go to home screen")
            {
                actions.Add(new CommandAction("device.navigate", new Dictionary<string, string> { ["action"] = "home" }));
                textResponse = "Navigating home.";
            }
            else if (lower == "go back" || lower == "back")
            {
                actions.Add(new CommandAction("device.navigate", new Dictionary<string, string> { ["action"] = "back" }));
                textResponse = "Navigating back.";
            }
            // App launch: "launch X", "open X", "start X", "run X"
            else if (lower.StartsWith("launch ") || lower.StartsWith("open ") ||
                     lower.StartsWith("start ") || lower.StartsWith("run "))
            {
                var appName = lower
                    .Replace("launch ", "").Replace("open ", "")
                    .Replace("start ", "").Replace("run ", "").Trim();
                actions.Add(new CommandAction("device.open_app", new Dictionary<string, string> { ["name"] = appName }));
                textResponse = $"Launching {appName}.";
            }
            // Media controls
            else if (lower.Contains("play") || lower.Contains("pause") || lower.Contains("stop") ||
                     lower.Contains("next") || lower.Contains("previous"))
            {
                var mediaAction = lower.Contains("play") ? "play" :
                                  lower.Contains("pause") ? "pause" :
                                  lower.Contains("stop") ? "stop" :
                                  lower.Contains("next") ? "next" : "previous";
                actions.Add(new CommandAction("device.media", new Dictionary<string, string> { ["action"] = mediaAction }));
                textResponse = $"Media: {mediaAction}.";
            }
            else
            {
                if (aiFallback != null)
                {
                    Console.WriteLine($"[command-api] No heuristic match — forwarding to Copilot AI: {commandRequest.Command}");
                    try
                    {
                        textResponse = await aiFallback(commandRequest.Command, CancellationToken.None);
                    }
                    catch (Exception aiEx)
                    {
                        textResponse = $"AI error: {aiEx.Message}";
                        Console.Error.WriteLine($"[command-api] AI fallback error: {aiEx.Message}");
                    }
                }
                else
                {
                    textResponse = $"Command '{commandRequest.Command}' received but no action matched.";
                    Console.WriteLine($"[command-api] No action matched for: {commandRequest.Command}");
                }
            }

            Console.WriteLine($"[command-api] Response: {textResponse} | Actions: {actions.Count}");

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

    private record CommandApiRequest(string Command, string DeviceToken, string? DeviceName = null);
    private record CommandAction(string Type, Dictionary<string, string>? Params);
    private record CommandApiResponse(string TextResponse, List<CommandAction>? Actions, bool Success);
}
