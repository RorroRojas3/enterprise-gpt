using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.BackgroundJobs.Models;

/// <summary>
/// What a background job is ingesting into, recorded so a cancel can undo it without guessing.
/// </summary>
/// <param name="Kind">Whether the document belongs to a conversation or a project.</param>
/// <param name="ParentId">The owning conversation or project.</param>
public sealed record JobTarget(JobTargetKinds Kind, Guid ParentId);
