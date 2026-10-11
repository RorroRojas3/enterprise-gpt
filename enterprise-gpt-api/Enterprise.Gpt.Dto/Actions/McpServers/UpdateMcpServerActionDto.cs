using Enterprise.Gpt.Common.Constants.McpServers;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Common.Validation;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.McpServers;

public record UpdateMcpServerActionDto
{
    public string Name { get; init; } = null!;

    public string Description { get; init; } = null!;

    public string Url { get; init; } = null!;

    public McpAuthTypes AuthType { get; init; }

    public string? Scope { get; init; }

    public string? IconKey { get; init; }

    /// <summary>
    /// Extra request headers to send on every call to this server, such as the remote Azure
    /// DevOps server's <c>X-MCP-Toolsets</c>; <see langword="null"/> or empty for none.
    /// </summary>
    /// <remarks>Full representation: an update that omits this clears the stored set.</remarks>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public class UpdateMcpServerActionDtoValidator : AbstractValidator<UpdateMcpServerActionDto>
{
    public UpdateMcpServerActionDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(McpServerLimits.NameMaxLength);
        RuleFor(x => x.Description)
            .NotEmpty()
            .MaximumLength(McpServerLimits.DescriptionMaxLength);
        RuleFor(x => x.Url)
            .NotEmpty()
            .MaximumLength(McpServerLimits.UrlMaxLength)
            .Must(BeAnAbsoluteHttpUri)
            .WithMessage("Url must be an absolute http or https URI.");
        RuleFor(x => x.AuthType)
            .IsInEnum();
        RuleFor(x => x.Scope)
            .NotEmpty()
            .MaximumLength(McpServerLimits.ScopeMaxLength)
            .When(x => x.AuthType == McpAuthTypes.EntraIdOnBehalfOf)
            .WithMessage($"Scope is required when the auth type is {nameof(McpAuthTypes.EntraIdOnBehalfOf)}.");
        // "Every type but one", so a new auth type inherits the refusal instead of escaping it.
        RuleFor(x => x.Scope)
            .Empty()
            .When(x => x.AuthType != McpAuthTypes.EntraIdOnBehalfOf)
            .WithMessage($"Scope is only used by the {nameof(McpAuthTypes.EntraIdOnBehalfOf)} auth type and must be empty for the others.");
        // A slug, not an enum, so a new client icon needs no server deploy; unknown keys get a generic glyph.
        // Empty is rejected rather than stored as '' beside the NULLs meaning the same; null clears the icon.
        RuleFor(x => x.IconKey)
            .MaximumLength(McpServerLimits.IconKeyMaxLength)
            .Matches(IconKeyPattern)
            .When(x => x.IconKey is not null)
            .WithMessage("IconKey must be a lowercase slug such as 'microsoft'.");
        // Custom so every problem gets its own line under one `Headers` key, not just the first.
        RuleFor(x => x.Headers)
            .Custom((headers, context) =>
            {
                foreach (var message in McpServerHeaderRules.Validate(headers))
                {
                    context.AddFailure(message);
                }
            });
    }

    private const string IconKeyPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    #region Private methods

    private static bool BeAnAbsoluteHttpUri(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    #endregion
}
