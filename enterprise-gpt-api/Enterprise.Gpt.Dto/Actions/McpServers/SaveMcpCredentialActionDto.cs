using Enterprise.Gpt.Common.Constants.McpServers;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.McpServers;

/// <summary>
/// The credential a user supplies for an MCP server registered with
/// <c>McpAuthTypes.UserApiKey</c>.
/// </summary>
public record SaveMcpCredentialActionDto
{
    /// <summary>
    /// The token exactly as the third party issued it, such as a GitHub personal access token.
    /// </summary>
    /// <remarks>
    /// Named <c>ApiKey</c> so the access log's default redaction list already covers it. Renaming
    /// it would put the value in a log line.
    /// </remarks>
    public string ApiKey { get; init; } = null!;
}

public class SaveMcpCredentialActionDtoValidator : AbstractValidator<SaveMcpCredentialActionDto>
{
    public SaveMcpCredentialActionDtoValidator()
    {
        RuleFor(x => x.ApiKey)
            .NotEmpty()
            .MinimumLength(UserMcpCredentialLimits.ApiKeyMinLength)
            .MaximumLength(UserMcpCredentialLimits.ApiKeyMaxLength)
            .Must(McpCredentialRules.IsWellFormed)
            .WithMessage("Enter the token exactly as it was issued, with no spaces or line breaks.");
    }
}
