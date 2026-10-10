using Enterprise.Gpt.Common.Enums;
using FluentValidation;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto.Actions.Conversations;

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
