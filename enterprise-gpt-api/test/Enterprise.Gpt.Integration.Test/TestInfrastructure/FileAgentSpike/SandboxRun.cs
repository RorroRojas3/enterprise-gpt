using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// The outcome of one instruction sent to the sandbox.
/// </summary>
/// <param name="ConversationId">The server-side conversation, when the provider keeps one. Recorded
/// as the documented alternative to re-sending history, should the container turn out not to survive
/// on round-tripped messages.</param>
/// <param name="ObservedContentKinds">Every distinct content shape the response carried, with the CLR
/// type of each raw representation — what the Responses route returns, as against what the SDK
/// documents.</param>
/// <param name="Logs">Standard output as the sandbox reported it. Probe results are read from here and
/// from files the script writes, never from <paramref name="Text"/>: a model summarising its own run
/// can round a version, drop a library, or call a conversion successful because nothing raised.</param>
internal sealed record SandboxRun(
    string Text,
    IReadOnlyList<SandboxArtifact> Artifacts,
    string? ContainerId,
    string? ConversationId,
    IReadOnlyList<string> ObservedContentKinds,
    IReadOnlyList<string> Logs,
    ChatResponse Response);
