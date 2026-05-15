using FluentValidation;

namespace RR.AI_Chat.Dto.Actions.Chat
{
    public class CreateConversationActionDto
    {
    }

    public class CreateConversationActionDtoValidator : AbstractValidator<CreateConversationActionDto>
    {
        public CreateConversationActionDtoValidator()
        {
        }
    }

    public class CreateConversationStreamActionDto
    {
        public string Prompt { get; set; } = null!;

        public Guid ModelId { get; set; }

        public List<McpDto> McpServers { get; set; } = [];
    }

    public class CreateConversationStreamActionDtoValidator : AbstractValidator<CreateConversationStreamActionDto>
    {
        public CreateConversationStreamActionDtoValidator()
        {
            RuleFor(x => x.Prompt)
                .NotEmpty().WithMessage("Prompt is required.");
            RuleFor(x => x.ModelId)
                .NotEmpty().WithMessage("ModelId is required.");
            RuleForEach(x => x.McpServers)
                .ChildRules(mcp =>
                {
                    mcp.RuleFor(m => m.Name)
                        .NotEmpty().WithMessage("MCP Server name is required.");
                })
                .When(x => x.McpServers != null && x.McpServers.Count > 0);
        }
    }

    public class DeactivateConversationsBulkActionDto
    {
        public List<Guid> ChatIds { get; set; } = [];
    }

    public class DeactivateConversationsBulkActionDtoValidator : AbstractValidator<DeactivateConversationsBulkActionDto>
    {
        public DeactivateConversationsBulkActionDtoValidator()
        {
            RuleFor(x => x.ChatIds).NotEmpty();
            RuleForEach(x => x.ChatIds).NotEmpty();
        }
    }

    public class UpdateConversationActionDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = null!;

        public Guid? ProjectId { get; set; }
    }

    public class UpdateConversationActionDtoValidator : AbstractValidator<UpdateConversationActionDto>
    {
        public UpdateConversationActionDtoValidator()
        {
            RuleFor(x => x.Id).NotEmpty();
            RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
        }
    }
}
