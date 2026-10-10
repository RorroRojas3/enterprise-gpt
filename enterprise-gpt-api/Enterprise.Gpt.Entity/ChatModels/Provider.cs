using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    [Table(nameof(Provider), Schema = "Core.Ref")]
    public class Provider : BaseEntity
    {
        [StringLength(128)]
        public string Name { get; set; } = null!;
    }
}
