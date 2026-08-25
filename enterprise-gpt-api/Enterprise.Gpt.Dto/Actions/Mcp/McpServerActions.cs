using FluentValidation;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto.Actions.Mcp
{
    public record CreateMcpServerActionDto
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
        /// <remarks>
        /// Both verbs take a full representation, so an update that omits this clears the stored
        /// set — the same rule every other field on this DTO follows.
        /// </remarks>
        public IReadOnlyDictionary<string, string>? Headers { get; init; }
    }

    public class CreateMcpServerActionDtoValidator : AbstractValidator<CreateMcpServerActionDto>
    {
        public CreateMcpServerActionDtoValidator()
        {
            RuleFor(x => x.Name)
                .NotEmpty()
                .MaximumLength(256);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(1024);
            RuleFor(x => x.Url)
                .NotEmpty()
                .MaximumLength(2048)
                .Must(BeAnAbsoluteHttpUri)
                .WithMessage("Url must be an absolute http or https URI.");
            RuleFor(x => x.AuthType)
                .IsInEnum();
            RuleFor(x => x.Scope)
                .NotEmpty()
                .MaximumLength(512)
                .When(x => x.AuthType == McpAuthTypes.EntraIdOnBehalfOf)
                .WithMessage($"Scope is required when the auth type is {nameof(McpAuthTypes.EntraIdOnBehalfOf)}.");
            RuleFor(x => x.Scope)
                .Empty()
                .When(x => x.AuthType == McpAuthTypes.None)
                .WithMessage($"Scope must be empty when the auth type is {nameof(McpAuthTypes.None)}.");
            // A slug, not an enum: the artwork lives in the client, so shipping a new icon must
            // not require a server deploy. A key the client does not recognise degrades to its
            // generic glyph, which is why an unknown-but-well-formed value is accepted here.
            // `is not null` rather than `IsNullOrEmpty`: an empty string would otherwise be
            // stored as '' beside the NULLs meaning the same thing, and `IconKey IS NULL` and
            // `iconKey === null` would disagree about the same row. Null clears the icon.
            RuleFor(x => x.IconKey)
                .MaximumLength(64)
                .Matches(IconKeyPattern)
                .When(x => x.IconKey is not null)
                .WithMessage("IconKey must be a lowercase slug such as 'microsoft'.");
            // Custom rather than a chain of Must rules: the rules are shared with the other
            // validator and with the column width, and this reports every problem in the set on
            // its own line under one `Headers` key instead of collapsing them into the first.
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

        private static bool BeAnAbsoluteHttpUri(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }

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
        /// <remarks>
        /// Both verbs take a full representation, so an update that omits this clears the stored
        /// set — the same rule every other field on this DTO follows.
        /// </remarks>
        public IReadOnlyDictionary<string, string>? Headers { get; init; }
    }

    public class UpdateMcpServerActionDtoValidator : AbstractValidator<UpdateMcpServerActionDto>
    {
        public UpdateMcpServerActionDtoValidator()
        {
            RuleFor(x => x.Name)
                .NotEmpty()
                .MaximumLength(256);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(1024);
            RuleFor(x => x.Url)
                .NotEmpty()
                .MaximumLength(2048)
                .Must(BeAnAbsoluteHttpUri)
                .WithMessage("Url must be an absolute http or https URI.");
            RuleFor(x => x.AuthType)
                .IsInEnum();
            RuleFor(x => x.Scope)
                .NotEmpty()
                .MaximumLength(512)
                .When(x => x.AuthType == McpAuthTypes.EntraIdOnBehalfOf)
                .WithMessage($"Scope is required when the auth type is {nameof(McpAuthTypes.EntraIdOnBehalfOf)}.");
            RuleFor(x => x.Scope)
                .Empty()
                .When(x => x.AuthType == McpAuthTypes.None)
                .WithMessage($"Scope must be empty when the auth type is {nameof(McpAuthTypes.None)}.");
            // A slug, not an enum: the artwork lives in the client, so shipping a new icon must
            // not require a server deploy. A key the client does not recognise degrades to its
            // generic glyph, which is why an unknown-but-well-formed value is accepted here.
            // `is not null` rather than `IsNullOrEmpty`: an empty string would otherwise be
            // stored as '' beside the NULLs meaning the same thing, and `IconKey IS NULL` and
            // `iconKey === null` would disagree about the same row. Null clears the icon.
            RuleFor(x => x.IconKey)
                .MaximumLength(64)
                .Matches(IconKeyPattern)
                .When(x => x.IconKey is not null)
                .WithMessage("IconKey must be a lowercase slug such as 'microsoft'.");
            // Custom rather than a chain of Must rules: the rules are shared with the other
            // validator and with the column width, and this reports every problem in the set on
            // its own line under one `Headers` key instead of collapsing them into the first.
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

        private static bool BeAnAbsoluteHttpUri(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
