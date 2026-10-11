using Enterprise.Gpt.Service.Options;
using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

/// <summary>
/// Azure AI Foundry chat client settings, validated at startup when <see cref="Enabled"/> is set.
/// </summary>
/// <remarks>
/// Chat Completions takes no reasoning parameters, so this provider cannot request the reasoning summary
/// the activity timeline renders; a deployment that needs it belongs on <see cref="AzureOpenAIOptions"/>.
/// </remarks>
public sealed class AzureAIFoundryOptions : AzureV1EndpointOptions
{
    public const string SectionName = "AzureAIFoundry";

    /// <summary>Whether this provider is registered at all.</summary>
    /// <remarks>
    /// When false, a model row on this provider fails with <c>ProviderNotConfiguredException</c> (503).
    /// </remarks>
    public bool Enabled { get; set; }
}

internal sealed class AzureAIFoundryOptionsValidator : AbstractValidator<AzureAIFoundryOptions>
{
    public AzureAIFoundryOptionsValidator()
    {
        When(options => options.Enabled, () =>
        {
            RuleFor(options => options.Url).Must((options, _) => options.IsUrlAbsolute)
                .WithMessage("AzureAIFoundry:Url must be an absolute http or https URI when AzureAIFoundry:Enabled is true, for example https://my-resource.services.ai.azure.com/.");
            RuleFor(options => options.Url).Must((options, _) => options.IsUrlResourceRoot)
                .WithMessage("AzureAIFoundry:Url must be the resource root, without /openai/v1 — the client appends it — when AzureAIFoundry:Enabled is true.");
            RuleFor(options => options.ApiKey).NotEmpty()
                .WithMessage("AzureAIFoundry:ApiKey is required when AzureAIFoundry:Enabled is true.");
            RuleFor(options => options.DefaultModel).NotEmpty()
                .WithMessage("AzureAIFoundry:DefaultModel is required when AzureAIFoundry:Enabled is true.");
        });
    }
}
