using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>One file a sandbox run produced, as the response described it.</summary>
/// <param name="FileId">The hosted file's id.</param>
/// <param name="ContainerId">
/// The code interpreter container the file lives in. Without it a download resolves against the
/// standard Files API, which cannot see a file the sandbox wrote, and answers 404.
/// </param>
internal sealed record SandboxArtifact(string FileId, string? Name, string? ContainerId);
