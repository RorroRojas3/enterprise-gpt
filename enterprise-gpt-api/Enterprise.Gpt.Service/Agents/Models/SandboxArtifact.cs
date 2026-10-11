namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>One file a sandbox run produced, as the response described it.</summary>
/// <param name="FileId">The hosted file's id.</param>
/// <param name="Name">The file name from any channel; <see langword="null"/> leaves the file refused.</param>
/// <param name="ContainerId">
/// The code interpreter container; without it a download hits the standard Files API and 404s.
/// </param>
internal sealed record SandboxArtifact(string FileId, string? Name, string? ContainerId);
