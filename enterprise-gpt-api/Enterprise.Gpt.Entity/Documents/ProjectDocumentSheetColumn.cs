using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// A column of a sheet belonging to a project document.
/// </summary>
[Table(nameof(ProjectDocumentSheetColumn), Schema = "Core")]
public class ProjectDocumentSheetColumn : BaseDocumentSheetColumn
{
    /// <summary>The sheet this column belongs to.</summary>
    public Guid ProjectDocumentSheetId { get; set; }

    public ProjectDocumentSheet ProjectDocumentSheet { get; set; } = null!;
}
