using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// A worksheet of a spreadsheet uploaded into a project.
/// </summary>
[Table(nameof(ProjectDocumentSheet), Schema = "Core")]
public class ProjectDocumentSheet : BaseDocumentSheet
{
    /// <summary>The document this sheet belongs to.</summary>
    public Guid ProjectDocumentId { get; set; }

    public ProjectDocument ProjectDocument { get; set; } = null!;

    /// <summary>The sheet's columns, in left-to-right order.</summary>
    public List<ProjectDocumentSheetColumn> Columns { get; set; } = [];

    /// <summary>The sheet's data rows, in reading order.</summary>
    public List<ProjectDocumentSheetRow> Rows { get; set; } = [];
}
