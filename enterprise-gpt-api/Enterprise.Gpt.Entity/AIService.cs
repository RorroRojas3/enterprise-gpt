using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    [Table(nameof(AIService), Schema = "Core.Ref")]
    public class AIService : BaseEntity
    {
        [StringLength(25)]
        public string Name { get; set; } = null!;
    }
}
