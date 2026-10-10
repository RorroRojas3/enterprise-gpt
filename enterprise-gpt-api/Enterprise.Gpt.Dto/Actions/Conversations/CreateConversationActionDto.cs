using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Conversations;

public class CreateConversationActionDto
{
    /// <summary>
    /// The project to create the conversation inside, or <see langword="null"/> for a standalone
    /// conversation. The project must be active and owned by the caller.
    /// </summary>
    public Guid? ProjectId { get; set; }
}

public class CreateConversationActionDtoValidator : AbstractValidator<CreateConversationActionDto>
{
    public CreateConversationActionDtoValidator()
    {
        // Only the "omitted" and "real id" shapes are meaningful; an explicit empty guid is a
        // client bug that would otherwise reach the ownership check and return a confusing 404.
        RuleFor(x => x.ProjectId)
            .NotEmpty()
            .When(x => x.ProjectId.HasValue);
    }
}
