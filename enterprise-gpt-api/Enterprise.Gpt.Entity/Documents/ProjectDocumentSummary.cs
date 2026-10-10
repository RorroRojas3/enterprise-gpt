using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// The canonical summary of a document uploaded into a project.
/// </summary>
/// <remarks>
/// Shared, not per-conversation: a project document is readable by every conversation in the
/// project, so the first conversation to summarize it pays and every later one reads the same row
/// for free.
/// </remarks>
[Table(nameof(ProjectDocumentSummary), Schema = "Core")]
public class ProjectDocumentSummary : BaseDocumentSummary
{
    /// <summary>The document this summarizes.</summary>
    public Guid ProjectDocumentId { get; set; }

    public ProjectDocument ProjectDocument { get; set; } = null!;
}
