using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RR.AI_Chat.Entity
{
    public class BaseDocument : BaseModifiedEntity
    {
        [ForeignKey(nameof(User))]
        public Guid UserId { get; set; }

        [StringLength(256)]
        public string Name { get; set; } = null!;

        [StringLength(8)]
        public string Extension { get; set; } = null!;

        [StringLength(256)]
        public string MimeType { get; set; } = null!;

        public long Size { get; set; }

        [Column(TypeName = "nvarchar(max)")]
        public string Path { get; set; } = null!;

        public User User { get; set; } = null!;
    }
}
