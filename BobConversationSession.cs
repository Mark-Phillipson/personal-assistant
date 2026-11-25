using GitHub.Copilot;
using Microsoft.Extensions.AI;

/// <summary>
/// Owns Bob's one cross-channel Copilot conversation. The stable session ID lets
/// Copilot restore the transcript after Bob restarts; the in-process gate keeps
/// Telegram, Talon/CLI and Android turns from overlapping.
/// </summary>
internal static class BobConversationSession
{
    public const string SessionId = "bob-primary-conversation";

    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private static readonly SemaphoreSlim TurnGate = new(1, 1);
    private static CopilotClient? _owner;
    private static CopilotSession? _session;

    public static async Task<CopilotSession> GetOrCreateAsync(
        CopilotClient client,
        ICollection<AIFunctionDeclaration> tools,
        SessionConfig createConfig,
        string context,
        CancellationToken cancellationToken = default)
    {
        await InitializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null && ReferenceEquals(_owner, client))
            {
                await TurnGate.WaitAsync(cancellationToken);
                try
                {
#pragma warning disable GHCP001 // Required to re-register host-process tool callbacks on a resumed session.
                    await _session.SetToolsAsync(tools, cancellationToken);
#pragma warning restore GHCP001
                    return _session;
                }
                finally
                {
                    TurnGate.Release();
                }
            }

            if (_session is not null)
            {
                await _session.DisposeAsync();
                _session = null;
                _owner = null;
            }

            CopilotSession session;
            try
            {
                session = await client.ResumeSessionAsync(
                    SessionId,
                    new ResumeSessionConfig
                    {
                        OnPermissionRequest = PermissionHandler.ApproveAll
                    });

                // Tool callbacks live in the host process, so re-register them
                // whenever the persisted conversation is resumed.
#pragma warning disable GHCP001 // Required to re-register host-process tool callbacks on a resumed session.
                await session.SetToolsAsync(tools, cancellationToken);
#pragma warning restore GHCP001
                Console.WriteLine($"[copilot.session.resume] id={SessionId} context={context}");
            }
            catch (Exception ex) when (IsMissingSession(ex))
            {
                createConfig.SessionId = SessionId;
                createConfig.Tools = tools;
                session = await client.CreateSessionAsync(createConfig);
                Console.WriteLine($"[copilot.session.create] id={SessionId} context={context}");
            }

            try
            {
                await ModelSelectionGuard.EnsureSessionUsesConfiguredModelAsync(
                    session,
                    SystemPromptBuilder.GetConfiguredModel(),
                    context,
                    cancellationToken);
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }

            _owner = client;
            _session = session;
            return session;
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    public static async Task<T> RunTurnAsync<T>(
        CopilotSession session,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await TurnGate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            TurnGate.Release();
        }
    }

    public static async Task<AssistantMessageEvent?> SendAndWaitAsync(
        CopilotSession session,
        MessageOptions options,
        CancellationToken cancellationToken)
    {
        return await RunTurnAsync(
            session,
            () => session.SendAndWaitAsync(options, null, cancellationToken),
            cancellationToken);
    }

    public static async Task ResetAsync(CopilotClient client, CopilotSession? session = null)
    {
        await InitializationGate.WaitAsync();
        await TurnGate.WaitAsync();
        try
        {
            var current = _session;
            _session = null;
            _owner = null;

            if (current is not null)
            {
                await current.DisposeAsync();
            }
            else if (session is not null)
            {
                await session.DisposeAsync();
            }

            await client.DeleteSessionAsync(SessionId);
            Console.WriteLine($"[copilot.session.reset] id={SessionId}");
        }
        finally
        {
            TurnGate.Release();
            InitializationGate.Release();
        }
    }

    public static async Task InvalidateAsync(CopilotSession session)
    {
        await InitializationGate.WaitAsync();
        await TurnGate.WaitAsync();
        try
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
                _owner = null;
            }

            await session.DisposeAsync();
        }
        finally
        {
            TurnGate.Release();
            InitializationGate.Release();
        }
    }

    private static bool IsMissingSession(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("session not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("session does not exist", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no session with", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unknown session", StringComparison.OrdinalIgnoreCase);
    }
}
