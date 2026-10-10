using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Entity.Base;

public class BaseModifiedByEntity : BaseModifiedEntity
{
    [JsonPropertyName("createdBy")]
    [ForeignKey(nameof(CreatedBy))]
    public Guid CreatedById { get; set; }

    [JsonPropertyName("modifiedBy")]
    [ForeignKey(nameof(ModifiedBy))]
    public Guid ModifiedById { get; set; }

    public User CreatedBy { get; set; } = null!;

    public User ModifiedBy { get; set; } = null!;
}
