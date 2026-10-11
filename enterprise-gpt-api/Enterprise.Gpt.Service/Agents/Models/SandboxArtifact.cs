namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>One file a sandbox run produced, as the response described it.</summary>
/// <param name="FileId">The hosted file's id.</param>
/// <param name="Name">The file name the sandbox gave it, from whichever channel carried one, or <see langword="null"/> when none did, which leaves the file refused rather than stored.</param>
/// <param name="ContainerId">
/// The code interpreter container the file lives in. Without it a download resolves against the
/// standard Files API, which cannot see a file the sandbox wrote, and answers 404.
/// </param>
internal sealed record SandboxArtifact(string FileId, string? Name, string? ContainerId);
