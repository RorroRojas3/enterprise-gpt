using Amazon;
using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

/// <summary>
/// Amazon Bedrock chat provider settings, validated at startup when <see cref="Enabled"/> is set.
/// </summary>
/// <remarks>Free of AWS SDK types so the service layer takes no AWS package reference.</remarks>
public sealed class AmazonBedrockOptions
{
    public const string SectionName = "AmazonBedrock";

    /// <summary>Whether the Bedrock chat client is registered.</summary>
    /// <remarks>
    /// The flag, not the section's presence, gates registration and validation, so a partially
    /// filled-in section fails loudly instead of half-working.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>AWS region system name, for example <c>us-east-1</c>.</summary>
    /// <remarks>
    /// One region serves every model; cross-region capacity comes from an inference profile id
    /// (<c>us.anthropic.claude-…</c>) in the deployment name, not a second client.
    /// </remarks>
    public string Region { get; set; } = string.Empty;

    /// <summary>Bedrock API key sent as an HTTP bearer token.</summary>
    /// <remarks>
    /// Must be a long-lived key kept in user secrets or Key Vault: the token is supplied once at
    /// client construction and never refreshed.
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Fallback model id the SDK requires at construction; every turn sets the model from the catalog.
    /// </summary>
    public string DefaultModelId { get; set; } = string.Empty;
}

internal sealed class AmazonBedrockOptionsValidator : AbstractValidator<AmazonBedrockOptions>
{
    public AmazonBedrockOptionsValidator()
    {
        When(options => options.Enabled, () =>
        {
            RuleFor(options => options.ApiKey).NotEmpty()
                .WithMessage("AmazonBedrock:ApiKey is required when AmazonBedrock:Enabled is true.");
            RuleFor(options => options.DefaultModelId).NotEmpty()
                .WithMessage("AmazonBedrock:DefaultModelId is required when AmazonBedrock:Enabled is true.");
            // Not GetBySystemName: it fabricates an endpoint for an unknown name, turning a typo into a DNS error.
            // The list is baked into the pinned AWSSDK.Core, so a newer region fails here until it is bumped.
            RuleFor(options => options.Region)
                .Must(region => RegionEndpoint.EnumerableAllRegions.Any(endpoint => endpoint.SystemName == region))
                .WithMessage("AmazonBedrock:Region must be a known AWS region system name, for example us-east-1. Regions added to AWS after the pinned AWSSDK.Core version are not recognized until it is upgraded.");
        });
    }
}
