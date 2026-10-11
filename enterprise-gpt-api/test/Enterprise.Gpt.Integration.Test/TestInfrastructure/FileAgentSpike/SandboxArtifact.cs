namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// One artifact a sandbox run produced, together with the channel it was found on.
/// </summary>
/// <param name="Channel">Kept rather than normalised away: which channel carries a produced file is
/// what the spike is measuring.</param>
/// <param name="FileId">The hosted file's id, or <see langword="null"/> for inline bytes, which have none.</param>
/// <param name="Name">The file name the channel reported, a citation's title standing in for one; <see langword="null"/> when it named none.</param>
/// <param name="MediaType">The content type the channel declared, or <see langword="null"/> when it declared none, as a citation never does.</param>
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
