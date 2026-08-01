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
        }

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
        }

        private static bool BeAnAbsoluteHttpUri(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
