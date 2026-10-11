using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Conversations;

public class DeactivateConversationsBulkActionDto
{
    public List<Guid> ConversationIds { get; set; } = [];
}

public class DeactivateConversationsBulkActionDtoValidator : AbstractValidator<DeactivateConversationsBulkActionDto>
{
    public DeactivateConversationsBulkActionDtoValidator()
    {
        RuleFor(x => x.ConversationIds).NotEmpty();
        RuleForEach(x => x.ConversationIds).NotEmpty();
    }
}
