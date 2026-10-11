using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.ChatModels;

public class Provider : BaseCreatedEntity<Guid>
{
    public string Name { get; set; } = null!;
}
