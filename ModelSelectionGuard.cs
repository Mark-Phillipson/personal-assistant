using GitHub.Copilot.SDK;

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
        await session.SetModelAsync(requested, cancellationToken);

        // Prefer explicit model RPC so we can read back the runtime model id.
        var switchResult = await session.Rpc.Model.SwitchToAsync(requested, cancellationToken);
        var current = await session.Rpc.Model.GetCurrentAsync(cancellationToken);

        var switchedTo = switchResult?.ModelId ?? string.Empty;
        var currentModelId = current?.ModelId ?? string.Empty;

        if (!ModelIdsMatch(requested, currentModelId))
        {
            throw new InvalidOperationException(
                $"Model lock failed ({context}). Requested '{requested}', switch result '{switchedTo}', active '{currentModelId}'. Refusing to continue to avoid unintended model usage.");
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