using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Chat
{
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
            RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
            RuleFor(x => x.ProjectId)
                .NotEmpty()
                .When(x => x.ProjectId.HasValue);
        }
    }
}
