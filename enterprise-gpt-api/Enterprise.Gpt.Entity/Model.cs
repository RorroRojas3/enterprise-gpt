using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    [Table(nameof(Model), Schema = "Core.Ref")]
    public class Model : BaseModifiedByEntity
    {
        [StringLength(25)]
        public string Name { get; set; } = null!;

        [StringLength(100)]
        public string Description { get; set; } = null!;

        public bool IsToolEnabled { get; set; }
    }
}
