using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Entity.Base;

public class BaseModifiedEntity : BaseEntity
{
    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; set; }
}
