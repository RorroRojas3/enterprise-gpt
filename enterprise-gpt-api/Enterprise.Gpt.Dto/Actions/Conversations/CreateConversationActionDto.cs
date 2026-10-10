using Enterprise.Gpt.Common.Enums;
using FluentValidation;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Sets whether a conversation is one of the caller's favourites.
    /// </summary>
    /// <remarks>
    /// Deliberately its own action rather than a field on <see cref="UpdateConversationActionDto"/>:
    /// that PUT is a full representation, so a client renaming a conversation without echoing the
    /// flag back would silently un-favourite it.
    /// </remarks>
    public class SetConversationFavoriteActionDto
    {
        public bool IsFavorite { get; set; }
    }

    /// <summary>
    /// Records, replaces or withdraws the caller's rating of an assistant message (US-1102).
    /// </summary>
    /// <remarks>
    /// A set, not a toggle: the body carries the state being asked for, so a retried or duplicated
    /// request cannot land on the opposite value. Re-submitting replaces the stored rating rather
    /// than adding to it, which is what a unique index on the projection row guarantees.
    /// </remarks>
    public class SetMessageFeedbackActionDto
    {
        /// <summary>
        /// The verdict, or <see langword="null"/> to withdraw a rating already recorded.
        /// </summary>
        /// <remarks>
        /// Withdrawing an absent rating is not an error: the route is a set, and asking for the
        /// state it is already in succeeds.
        /// </remarks>
        [JsonConverter(typeof(JsonStringEnumConverter<MessageFeedbackRatings>))]
        public MessageFeedbackRatings? Rating { get; set; }
    }

    /// <summary>
    /// Validates <see cref="SetMessageFeedbackActionDto"/>.
    /// </summary>
    /// <remarks>
    /// One rule, and it is load-bearing rather than defensive. <c>JsonStringEnumConverter</c>
    /// allows integer values by default, and Microsoft documents that as allowing <em>undefined</em>
    /// ones — so without this, <c>{"rating": 7}</c> binds to a value the enum does not define,
    /// passes every other check, and is written to both stores. It then comes back out of the
    /// transcript as a JSON number, because a string converter has no name to write for it, which
    /// breaks the wire format for every client reading that field.
    /// </remarks>
    public class SetMessageFeedbackActionDtoValidator : AbstractValidator<SetMessageFeedbackActionDto>
    {
        public SetMessageFeedbackActionDtoValidator()
        {
            RuleFor(x => x.Rating)
                .IsInEnum()
                .WithMessage("Rating must be one of: Up, Down.")
                .When(x => x.Rating.HasValue);
        }
    }
}
