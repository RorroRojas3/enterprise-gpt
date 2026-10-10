using Enterprise.Gpt.Common.Enums;
using FluentValidation;
using System.Text.Json.Serialization;

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
