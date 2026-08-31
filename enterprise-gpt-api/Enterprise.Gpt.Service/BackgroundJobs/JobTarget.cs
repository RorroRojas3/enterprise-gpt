namespace Enterprise.Gpt.Service.BackgroundJobs;

/// <summary>Wording every path that reports a cancelled job shares.</summary>
public static class JobMessages
{
    public const string Cancelled = "Cancelled";
}

/// <summary>Which kind of parent a background job is ingesting a document into.</summary>
public enum JobTargetKind
{
    Conversation = 1,
    Project = 2,
}

/// <summary>
/// What a background job is ingesting into, recorded so a cancel can undo it without guessing.
/// </summary>
/// <param name="Kind">Whether the document belongs to a conversation or a project.</param>
/// <param name="ParentId">The owning conversation or project.</param>
public sealed record JobTarget(JobTargetKind Kind, Guid ParentId);
