using GitHub.Copilot;

internal static class ModelSelectionGuard
{
    public static async Task EnsureSessionUsesConfiguredModelAsync(
        CopilotSession session,
        string configuredModel,
        string context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
        {
            throw new InvalidOperationException("ASSISTANT_MODEL is empty. Refusing to run without an explicit configured model.");
        }

        var requested = configuredModel.Trim();

        // Explicitly switch model at session start (equivalent to issuing /model first).
        await session.SetModelAsync(requested, new SetModelOptions(), cancellationToken);

        // Read back the runtime model id after the switch so we can verify it took effect.
        var current = await session.Rpc.Model.GetCurrentAsync(cancellationToken);
        var currentModelId = current?.ModelId ?? string.Empty;

        if (!ModelIdsMatch(requested, currentModelId))
        {
            throw new InvalidOperationException(
                $"Model lock failed ({context}). Requested '{requested}', active '{currentModelId}'. Refusing to continue to avoid unintended model usage.");
        }

        Console.WriteLine($"[model.lock] context={context} requested='{requested}' active='{currentModelId}'");
    }

    private static bool ModelIdsMatch(string requested, string active)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(active))
        {
            return false;
        }

        var normalizedRequested = NormalizeModelId(requested);
        var normalizedActive = NormalizeModelId(active);

        return normalizedRequested == normalizedActive
            || normalizedRequested.Contains(normalizedActive, StringComparison.Ordinal)
            || normalizedActive.Contains(normalizedRequested, StringComparison.Ordinal);
    }

    private static string NormalizeModelId(string model)
    {
        var chars = model
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();

        return new string(chars);
    }
}