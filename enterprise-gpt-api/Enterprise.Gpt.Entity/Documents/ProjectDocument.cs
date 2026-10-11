using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Projects;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// A document uploaded into a project, readable by every conversation in that project.
/// </summary>
/// <remarks>
/// A sibling of <see cref="ConversationDocument"/>, not a shared table with a nullable owner: visibility
/// rules differ, and retrieval should filter on an indexed foreign key.
/// </remarks>
public class ProjectDocument : BaseDocument<Guid>
{
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    public List<ProjectDocumentChunk> Chunks { get; set; } = [];

    public List<ProjectDocumentSheet> Sheets { get; set; } = [];
}
