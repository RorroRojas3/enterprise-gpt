using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// A chunk of a document uploaded into a project.
/// </summary>
public class ProjectDocumentChunk : BaseDocumentChunk<Guid>
{
    /// <summary>The document this chunk was extracted from.</summary>
    public Guid ProjectDocumentId { get; set; }

    public ProjectDocument ProjectDocument { get; set; } = null!;
}
