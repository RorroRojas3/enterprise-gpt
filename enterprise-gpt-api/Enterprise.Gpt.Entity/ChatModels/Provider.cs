using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.ChatModels
{
    [Table(nameof(Provider), Schema = "Core.Ref")]
    public class Provider : BaseEntity
    {
        [StringLength(128)]
        public string Name { get; set; } = null!;
    }
}
