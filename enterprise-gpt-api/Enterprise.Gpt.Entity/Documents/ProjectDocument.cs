using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Projects;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// A document uploaded into a project, readable by every conversation in that project.
/// </summary>
/// <remarks>
/// Deliberately a sibling of <see cref="ConversationDocument"/> rather than the same table with a
/// nullable owner: the two have different visibility rules, and a single table would make every
/// retrieval query filter on "which owner column is set" instead of on an indexed foreign key.
/// </remarks>
public class ProjectDocument : BaseDocument<Guid>
{
    /// <summary>The project that owns the document.</summary>
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>The retrieval units the document was split into, with their embeddings.</summary>
    public List<ProjectDocumentChunk> Chunks { get; set; } = [];

    /// <summary>The worksheets the document was read as, empty for a non-spreadsheet format.</summary>
    public List<ProjectDocumentSheet> Sheets { get; set; } = [];
}
