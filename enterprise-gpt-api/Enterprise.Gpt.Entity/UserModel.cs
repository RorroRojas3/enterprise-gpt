namespace Enterprise.Gpt.Entity
{
    public class UserModel : BaseModifiedByEntity
    {
        public Guid UserId { get; set; }

        public Guid ModelId { get; set; }

        public User User { get; set; } = null!;

        public Model Model { get; set; } = null!;
    }
}
