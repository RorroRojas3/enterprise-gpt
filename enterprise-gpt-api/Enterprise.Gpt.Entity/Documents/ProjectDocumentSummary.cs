using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// The canonical summary of a document uploaded into a project.
/// </summary>
/// <remarks>
/// Shared, not per-conversation: a project document is readable by every conversation in the
/// project, so the first conversation to summarize it pays and every later one reads the same row
/// for free.
/// </remarks>
public class ProjectDocumentSummary : BaseDocumentSummary<Guid>
{
    /// <summary>The document this summarizes.</summary>
    public Guid ProjectDocumentId { get; set; }

    public ProjectDocument ProjectDocument { get; set; } = null!;
}
