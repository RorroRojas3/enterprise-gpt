using FluentValidation;


namespace Enterprise.Gpt.Repository.Cosmos.Options;

/// <summary>
/// Connection and container settings for the conversation transcript store, bound from the
/// <c>CosmosDb</c> configuration section and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// Validated at startup because every setting here is read once, when a container handle is built:
/// a missing database or container id used to surface as a null-forgiving bang at registration
/// time and then as a failure on the first conversation, long after the deployment was declared
/// healthy.
/// </para>
/// <para>
/// <see cref="ConnectionString"/> carries an account key, which is the one place this API departs
/// from its <c>DefaultAzureCredential</c> preference. Moving to managed identity is a deployment
/// change with its own rollout and is deliberately out of scope; the type is shaped so an
/// endpoint-plus-credential pair can be added beside the connection string without moving anything
/// else.
/// </para>
/// </remarks>
public sealed class CosmosDbOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "CosmosDb";

    /// <summary>
    /// Gets or sets the Cosmos DB account connection string.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the database holding the transcript containers.
    /// </summary>
    public string DatabaseId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the container holding transcript header and message documents.
    /// </summary>
    /// <remarks>
    /// Partitioned on <c>/pk</c>, whose value is <c>{userId}:{conversationId}</c>. Pointing this at
    /// a container provisioned with any other key path fails startup rather than writing documents
    /// no read path can address.
    /// </remarks>
    public string TranscriptContainerId { get; set; } = string.Empty;

    /// <summary>
    /// The configuration key that named the pre-cutover container.
    /// </summary>
    public const string LegacyContainerKey = "CosmosDb:ContainerId";

    /// <summary>
    /// Gets or sets the pre-cutover container id, which must be unset.
    /// </summary>
    /// <remarks>
    /// Bound only so startup can reject a deployment that still sets it: ignored, a half-migrated
    /// configuration would start and every transcript would read as empty, which looks like data loss.
    /// </remarks>
    public string? ContainerId { get; set; }

    /// <summary>
    /// Gets or sets how many message documents a single transcript query page requests.
    /// Default: 100.
    /// </summary>
    /// <remarks>
    /// Bounds one page of a paged read and each iteration of the drain loop that replays history,
    /// so it trades round trips against per-response size. Cosmos's 4 MB response ceiling is the
    /// hard limit above it.
    /// </remarks>
    public int PageSize { get; set; } = 100;
}

internal sealed class CosmosDbOptionsValidator : AbstractValidator<CosmosDbOptions>
{
    public CosmosDbOptionsValidator()
    {
        // Messages name the key an operator edits and never echo the value: the connection string carries an account key.
        RuleFor(options => options.ConnectionString).NotEmpty().WithMessage("CosmosDb:ConnectionString is required.");
        RuleFor(options => options.DatabaseId).NotEmpty().WithMessage("CosmosDb:DatabaseId is required.");
        RuleFor(options => options.TranscriptContainerId).NotEmpty().WithMessage("CosmosDb:TranscriptContainerId is required.");
        RuleFor(options => options.PageSize).InclusiveBetween(1, 1000)
            .WithMessage("CosmosDb:PageSize must be between 1 and 1000.");
        // The cutover gate: a configuration still naming the pre-cutover container has not been migrated, and
        // starting it would answer every transcript read with an empty result.
        RuleFor(options => options.ContainerId).Null()
            .WithMessage($"{CosmosDbOptions.LegacyContainerKey} names the pre-cutover container, whose documents this application can no longer read. Remove the setting and set CosmosDb:TranscriptContainerId instead; see docs/operations/runbooks.md.");
    }
}
