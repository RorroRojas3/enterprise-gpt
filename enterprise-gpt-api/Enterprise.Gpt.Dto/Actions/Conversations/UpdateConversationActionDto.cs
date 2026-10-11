using Enterprise.Gpt.Common.Constants.Conversations;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Conversations;

public class UpdateConversationActionDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>
    /// The project the conversation should belong to after the update.
    /// </summary>
    /// <remarks>
    /// This is a full-representation PUT, so omitting the field (or sending
    /// <see langword="null"/>) removes the conversation from whatever project it is in. Clients
    /// that only mean to rename must echo the current project id back.
    /// </remarks>
    public Guid? ProjectId { get; set; }
}

public class UpdateConversationActionDtoValidator : AbstractValidator<UpdateConversationActionDto>
{
    public UpdateConversationActionDtoValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(ConversationLimits.NameMaxLength);
        RuleFor(x => x.ProjectId)
            .NotEmpty()
            .When(x => x.ProjectId.HasValue);
    }
}
