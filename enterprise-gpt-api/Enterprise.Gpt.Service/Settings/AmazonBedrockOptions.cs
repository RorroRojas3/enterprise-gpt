namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Connection settings for the Amazon Bedrock chat provider, bound from the <c>AmazonBedrock</c>
/// configuration section and validated at startup when <see cref="Enabled"/> is set.
/// </summary>
/// <remarks>
/// Deliberately free of AWS SDK types so the service layer takes no AWS package reference: the
/// region name is validated against <c>RegionEndpoint</c> where the client is registered.
/// </remarks>
public sealed class AmazonBedrockOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "AmazonBedrock";

    /// <summary>
    /// Gets or sets a value indicating whether the Bedrock chat client is registered.
    /// Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The flag, rather than the presence of configuration, is what gates registration and
    /// validation, so an environment that does not use Bedrock needs no Bedrock settings at all
    /// and a partially filled-in section fails loudly instead of half-working.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the AWS region system name the Bedrock runtime endpoint is resolved from, for
    /// example <c>us-east-1</c>.
    /// </summary>
    /// <remarks>
    /// One region serves every Bedrock-backed model. Cross-region capacity comes from putting an
    /// inference profile id (<c>us.anthropic.claude-…</c>) in the model's deployment name rather
    /// than from a second client.
    /// </remarks>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Amazon Bedrock API key presented as an HTTP bearer token.
    /// </summary>
    /// <remarks>
    /// A long-lived credential: keep it in user secrets locally and Key Vault when deployed, never
    /// in <c>appsettings.json</c>. Short-term keys are not usable here because the token is
    /// supplied once at client construction and never refreshed.
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Bedrock model id used when a request does not carry one.
    /// </summary>
    /// <remarks>
    /// Every chat turn sets the model explicitly from the catalog, so this is only the fallback the
    /// SDK requires to construct a client.
    /// </remarks>
    public string DefaultModelId { get; set; } = string.Empty;
}
