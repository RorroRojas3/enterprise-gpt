using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// One artifact a sandbox run produced, together with the channel it was found on.
/// </summary>
/// <param name="Channel">Kept rather than normalised away: which channel carries a produced file is
/// what the spike is measuring.</param>
/// <param name="ContainerId">Required to download the file — the standard Files API cannot see a
/// container-scoped one.</param>
/// <param name="InlineData">Bytes, for a channel that returned content rather than a reference. Never
/// set for a hosted file, which is fetched on demand so a large artifact is not held twice.</param>
internal sealed record SandboxArtifact(
    string Channel,
    string? FileId,
    string? Name,
    string? MediaType,
    string? ContainerId,
    byte[]? InlineData);

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
