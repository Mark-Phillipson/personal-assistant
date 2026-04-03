using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class CommandApiServer
{
    public static async Task StartAsync(
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<AiFallbackResponse>>? aiFallback = null)
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

    private static async Task HandleRequestAsync(HttpListenerContext context, string? expectedDeviceToken, Func<string, CancellationToken, Task<AiFallbackResponse>>? aiFallback)
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

            var commandText = commandRequest.Command.Trim();
            var lower = commandText.ToLowerInvariant();
            var actions = new List<CommandAction>();
            var textResponse = $"Received command: {commandText}";
            var success = true;

            Console.WriteLine($"[command-api] Command from '{commandRequest.DeviceName ?? "unknown"}': {commandText}");

            if (TryBuildRecognizedResponse(commandText, lower, actions, out var recognizedResponse))
            {
                textResponse = recognizedResponse;
            }
            else
            {
                if (aiFallback != null)
                {
                    Console.WriteLine($"[command-api] No heuristic match - forwarding to Copilot AI: {commandText}");
                    try
                    {
                        var fallbackResponse = await aiFallback(commandText, CancellationToken.None);
                        textResponse = fallbackResponse.TextResponse;
                        success = fallbackResponse.Success;

                        if (fallbackResponse.Actions is { Count: > 0 })
                        {
                            actions.AddRange(fallbackResponse.Actions);
                        }

                        if (!success)
                        {
                            response.StatusCode = (int)HttpStatusCode.BadGateway;
                        }
                    }
                    catch (Exception aiEx)
                    {
                        success = false;
                        response.StatusCode = (int)HttpStatusCode.BadGateway;
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

            response.StatusCode = response.StatusCode == 0 ? (int)HttpStatusCode.OK : response.StatusCode;
            await WriteJsonResponseAsync(response, new CommandApiResponse(textResponse, actions, success));
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

    private static bool TryBuildRecognizedResponse(
        string commandText,
        string lower,
        List<CommandAction> actions,
        out string textResponse)
    {
        if (TryExtractUrl(commandText, out var targetUrl))
        {
            actions.Add(new CommandAction("device.open_url", new Dictionary<string, string> { ["url"] = targetUrl }));
            textResponse = $"Opening URL {targetUrl}.";
            return true;
        }

        if (TryMatchNavigationAction(lower, out var navigationAction))
        {
            actions.Add(new CommandAction("device.navigate", new Dictionary<string, string> { ["action"] = navigationAction }));
            textResponse = $"Navigating {navigationAction}.";
            return true;
        }

        if (TryMatchScrollDirection(lower, out var scrollDirection))
        {
            actions.Add(new CommandAction("device.scroll", new Dictionary<string, string> { ["direction"] = scrollDirection }));
            textResponse = $"Scrolling {scrollDirection}.";
            return true;
        }

        if (TryExtractAppLaunchName(commandText, lower, out var appName))
        {
            actions.Add(new CommandAction("device.open_app", new Dictionary<string, string> { ["name"] = appName }));
            textResponse = $"Launching {appName}.";
            return true;
        }

        if (TryMatchMediaAction(lower, out var mediaAction))
        {
            actions.Add(new CommandAction("device.media", new Dictionary<string, string> { ["action"] = mediaAction }));
            textResponse = mediaAction == "play" ? "Resuming media." :
                mediaAction == "pause" ? "Pausing media." :
                mediaAction == "next" ? "Skipping to the next track." :
                "Going to the previous track.";
            return true;
        }

        textResponse = string.Empty;
        return false;
    }

    private static bool TryExtractUrl(string commandText, out string url)
    {
        var match = Regex.Match(commandText, @"https?://\S+", RegexOptions.IgnoreCase);
        url = match.Success ? match.Value.TrimEnd('.', ',', ';', ')', ']', '}') : string.Empty;
        return !string.IsNullOrWhiteSpace(url);
    }

    private static bool TryMatchNavigationAction(string lower, out string action)
    {
        action = lower switch
        {
            "go home" or "home" or "go to home screen" or "show home screen" => "home",
            "go back" or "back" => "back",
            "show recents" or "open recents" or "recent apps" or "show recent apps" => "recents",
            "show notifications" or "open notifications" or "notification shade" => "notifications",
            _ => string.Empty
        };

        return action.Length > 0;
    }

    private static bool TryMatchScrollDirection(string lower, out string direction)
    {
        direction = lower switch
        {
            "scroll up" or "swipe up" => "up",
            "scroll down" or "swipe down" => "down",
            "scroll left" or "swipe left" => "left",
            "scroll right" or "swipe right" => "right",
            _ => string.Empty
        };

        return direction.Length > 0;
    }

    private static bool TryExtractAppLaunchName(string commandText, string lower, out string appName)
    {
        appName = string.Empty;
        foreach (var verb in new[] { "launch ", "open ", "start ", "run " })
        {
            if (!lower.StartsWith(verb, StringComparison.Ordinal))
            {
                continue;
            }

            var payload = NormalizeAppLaunchPayload(commandText[verb.Length..]);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            if (payload.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || payload.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (payload.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
                || payload.StartsWith("my ", StringComparison.OrdinalIgnoreCase)
                || payload.StartsWith("a ", StringComparison.OrdinalIgnoreCase)
                || payload.StartsWith("an ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var wordCount = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount > 4)
            {
                return false;
            }

            appName = payload;
            return true;
        }

        return false;
    }

    private static string NormalizeAppLaunchPayload(string payload)
    {
        var normalized = payload.Trim();

        normalized = Regex.Replace(
            normalized,
            @"\b(on|in)\s+(my|the)\s+(phone|android|mobile|device)\b",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        normalized = Regex.Replace(
            normalized,
            @"\b(on|in)\s+(phone|android|mobile|device)\b",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        normalized = Regex.Replace(
            normalized,
            @"\b(please|now)\b",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        normalized = Regex.Replace(normalized, @"\s+", " ").Trim(' ', ',', '.', ';', ':');
        return normalized;
    }

    private static bool TryMatchMediaAction(string lower, out string action)
    {
        action = lower switch
        {
            "play" or "play music" or "resume" or "resume music" or "continue music" => "play",
            "pause" or "pause music" or "stop" or "stop music" => "pause",
            "next" or "next track" or "next song" or "skip" or "skip track" or "skip song" => "next",
            "previous" or "previous track" or "previous song" or "go previous" or "go to previous track" => "previous",
            _ => string.Empty
        };

        return action.Length > 0;
    }

    internal sealed record AiFallbackResponse(string TextResponse, List<CommandAction>? Actions, bool Success);
    internal sealed record CommandAction(string Type, Dictionary<string, string>? Params);

    private record CommandApiRequest(string Command, string DeviceToken, string? DeviceName = null);
    private record CommandApiResponse(string TextResponse, List<CommandAction>? Actions, bool Success);
}
