using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <remarks>
/// Shared across the project's conversations: the first to summarize pays, later ones reuse the row.
/// </remarks>
public class ProjectDocumentSummary : BaseDocumentSummary<Guid>
{
    public Guid ProjectDocumentId { get; set; }

    public ProjectDocument ProjectDocument { get; set; } = null!;
}
