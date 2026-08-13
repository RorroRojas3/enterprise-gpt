namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Connection and container settings for the Cosmos DB account holding conversation transcripts,
/// bound from the <c>CosmosDb</c> configuration section.
/// </summary>
/// <remarks>
/// Validated with <c>ValidateOnStart</c>, so a misconfigured deployment fails at boot rather than
/// on a user's first conversation.
/// </remarks>
public sealed class CosmosOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "CosmosDb";

    /// <summary>
    /// The account connection string.
    /// </summary>
    /// <remarks>
    /// An account key, which is the one place this application does not follow its
    /// <c>DefaultAzureCredential</c> preference. Moving to managed identity is a deployment change
    /// with its own rollout and is deliberately out of scope here; this property is the seam it
    /// would replace.
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The database holding <see cref="TranscriptContainerId"/>.
    /// </summary>
    public string DatabaseId { get; set; } = string.Empty;

    /// <summary>
    /// The container holding transcript documents, partitioned hierarchically on
    /// <c>/userId</c> then <c>/conversationId</c>.
    /// </summary>
    public string TranscriptContainerId { get; set; } = string.Empty;

    /// <summary>
    /// The retired single-document container setting.
    /// </summary>
    /// <remarks>
    /// Bound only so validation can reject it. Documents in that container are one transcript per
    /// item, a shape this application can no longer read, so a deployment that still names it must
    /// fail at boot rather than start against data it would misinterpret. Configuring it is the
    /// operator's signal that the cutover has not been performed.
    /// </remarks>
    public string? ContainerId { get; set; }
}
