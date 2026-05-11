Plan: Update GitHub.Copilot.SDK → 0.3.0

Summary
- Goal: Update `GitHub.Copilot.SDK` to `0.3.0` and mitigate breaking API changes so builds and tests remain green.
- Scope: migration touches session/model APIs, message send/await patterns, tool result parsing, attachments, and AIFunctionFactory usages.

Affected files (high level)
- `ModelSelectionGuard.cs`
- `Program.cs`
- `TelegramMessageHandler.cs`
- `AssistantToolsFactory.cs`
- Other call-sites that use `CopilotSession`, `MessageOptions`, `SendAndWaitAsync`, tool-result DTOs, or `AIFunctionFactory`.

Key SDK differences (notes from 0.3.0 XML)
- `session.Rpc.Model.SwitchToAsync` signature changed: (modelId, reason, capabilities, cancellationToken).
- `session.SetModelAsync` appears removed.
- `SendAndWaitAsync` helper is not present; `SendAsync` + event-driven pattern is shown in XML examples.
- Tool result shapes moved toward `ToolResultObject` / `ToolResultAIContent` instead of older `ToolExecutionCompleteDataResult` shapes.
- Attachment DTO names/JSON contract are in `ClientJsonContext` — some old concrete types may be missing.

Priority migration steps (recommended order)
1) Model switch (low-risk, high-impact)
   - Replace `session.SetModelAsync(...)` with RPC call using new signature.
   - Example: `await session.Rpc.Model.SwitchToAsync(requested, reason: null, capabilities: null, cancellationToken);`
2) Add `SendAndAwaitReplyAsync` helper and convert `SendAndWaitAsync` call-sites
   - Implement a helper that uses `session.SendAsync(...)` then awaits the next assistant message event via `session.On(evt => ...)` and a `TaskCompletionSource`.
   - Convert call-sites in `Program.cs` and `TelegramMessageHandler.cs` to use the helper.
3) Tool-result parsing
   - Replace parsing that assumed `ToolExecutionCompleteDataResult` with logic that handles `string` or `ToolResultObject` variants.
   - Use `ToolResultAIContent`/`ToolResultObject` and inspect `Contents` items (string / JsonElement / nested objects).
4) Message attachments
   - Audit `BuildMessageOptions` / `MessageOptions.Attachments` code.
   - If old `UserMessageDataAttachmentsItem*` types are missing, construct attachments using the SDK's JSON contract (or `JsonElement` fallback) until a concrete DTO is identified.
5) Audit `AIFunctionFactory.Create` usages
   - Confirm factory API is unchanged; update call signatures for 0.3.0 if necessary.
6) Run full build & tests; iterate on additional compile errors.

Migration patterns / snippets
- Model switch
```csharp
// Old
await session.Rpc.Model.SwitchToAsync(requested, cancellationToken);
// New
await session.Rpc.Model.SwitchToAsync(requested, reason: null, capabilities: null, cancellationToken);
```

- Send + await reply helper (concept)
```csharp
async Task<AssistantMessageEvent?> SendAndAwaitReplyAsync(CopilotSession session, MessageOptions opts, CancellationToken ct)
{
    var tcs = new TaskCompletionSource<AssistantMessageEvent?>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = session.On(evt => {
        if (evt is AssistantMessageEvent a && a.Data?.Content != null)
            tcs.TrySetResult(a);
    });
    await session.SendAsync(opts, ct);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
    linked.CancelAfter(TimeSpan.FromSeconds(30)); // tune timeout
    return await tcs.Task.WaitAsync(linked.Token);
}
```

- Tool result parsing (concept)
```csharp
if (evt.Data?.Result is string s) { /* plain text result */ }
else if (evt.Data?.Result is ToolResultObject tro) {
   // iterate tro.Contents or tro.Value depending on shape
}
```

Testing & verification (repeat after each set of changes)
```powershell
dotnet restore
dotnet build "personal-assistant.sln" -c Debug -p:UseAppHost=false -p:OutDir=bin\Debug\net10.0-verify\
dotnet test PersonalAssistant.Tests\PersonalAssistant.Tests.csproj -c Debug -p:UseAppHost=false -p:OutDir=PersonalAssistant.Tests\bin\Debug\net10.0-verify\
```

Risks & mitigations
- Concurrency/timeouts when replacing `SendAndWaitAsync` with event-based awaiting — use timeouts and robust TaskCompletionSource usage.
- Large refactor across many sites (`AIFunctionFactory`) could introduce signature mismatches — update incrementally and run tests.
- Attachment DTO changes may need temporary JSON-fallbacks.

Next actions (choose)
- A: Implement the `ModelSelectionGuard.cs` change (quick, I can patch and run tests).
- B: Implement `SendAndAwaitReplyAsync` helper and convert `SendAndWaitAsync` call-sites (medium effort).
- C: Patch `Program.cs` tool-result parsing to accept `ToolResultObject` shapes.
- D: Full compile & test after A+B+C and fix remaining errors iteratively.

Contact
- Notes: keep `Nerdbank.MessagePack` pin until transitive dependencies are re-evaluated post-migration.

-- End --
