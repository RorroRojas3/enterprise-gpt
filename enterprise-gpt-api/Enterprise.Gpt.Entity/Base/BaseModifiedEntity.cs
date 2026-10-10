using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Entity.Base;

public class BaseModifiedEntity : BaseEntity
{
    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; set; }
}
