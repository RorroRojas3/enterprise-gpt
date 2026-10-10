using System.ComponentModel.DataAnnotations.Schema;
using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// A chunk of a document uploaded into a project.
/// </summary>
[Table(nameof(ProjectDocumentChunk), Schema = "Core")]
public class ProjectDocumentChunk : BaseDocumentChunk
{
    /// <summary>The document this chunk was extracted from.</summary>
    public Guid ProjectDocumentId { get; set; }

    public ProjectDocument ProjectDocument { get; set; } = null!;
}
