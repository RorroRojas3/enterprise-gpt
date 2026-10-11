using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

public class ProjectDocumentSheet : BaseDocumentSheet<Guid>
{
    public Guid ProjectDocumentId { get; set; }

    public ProjectDocument ProjectDocument { get; set; } = null!;

    public List<ProjectDocumentSheetColumn> Columns { get; set; } = [];

    public List<ProjectDocumentSheetRow> Rows { get; set; } = [];
}
