using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Chat
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

    /// <summary>
    /// Identifies an MCP server selected for a chat request. Deliberately separate from the
    /// response-shaped <see cref="McpDto"/>: a request only needs the id, and reusing the
    /// response DTO would advertise <c>Name</c> as required in the OpenAPI document while the
    /// validator does not enforce it.
    /// </summary>
    public class McpServerSelectionDto
    {
        public Guid Id { get; set; }
    }

    public class CreateConversationStreamActionDto
    {
        public string Prompt { get; set; } = null!;

        public Guid ModelId { get; set; }

        public List<McpServerSelectionDto> McpServers { get; set; } = [];
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
                    mcp.RuleFor(m => m.Id)
                        .NotEmpty().WithMessage("MCP Server id is required.");
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
