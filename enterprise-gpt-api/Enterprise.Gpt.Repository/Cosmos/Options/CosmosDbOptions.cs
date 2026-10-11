using FluentValidation;


namespace Enterprise.Gpt.Repository.Cosmos.Options;

/// <summary>
/// Transcript store settings, bound from the <c>CosmosDb</c> section and validated at startup.
/// </summary>
/// <remarks>
/// <see cref="ConnectionString"/> carries an account key, a deliberate exception to the
/// <c>DefaultAzureCredential</c> preference until a managed-identity rollout.
/// </remarks>
public sealed class CosmosDbOptions
{
    public const string SectionName = "CosmosDb";

    public string ConnectionString { get; set; } = string.Empty;

    public string DatabaseId { get; set; } = string.Empty;

    /// <summary>Gets or sets the container holding transcript header and message documents.</summary>
    /// <remarks>
    /// Partitioned on <c>/pk</c> (<c>{userId}:{conversationId}</c>); any other key path fails startup.
    /// </remarks>
    public string TranscriptContainerId { get; set; } = string.Empty;

    /// <summary>The configuration key that named the pre-cutover container.</summary>
    public const string LegacyContainerKey = "CosmosDb:ContainerId";

    /// <summary>Gets or sets the pre-cutover container id, which must be unset.</summary>
    /// <remarks>
    /// Bound only so startup can reject it: a half-migrated configuration would read every transcript as empty.
    /// </remarks>
    public string? ContainerId { get; set; }

    /// <summary>Gets or sets how many message documents one transcript query page requests.</summary>
    /// <remarks>Trades round trips against response size; Cosmos caps a response at 4 MB.</remarks>
    public int PageSize { get; set; } = 100;
}

internal sealed class CosmosDbOptionsValidator : AbstractValidator<CosmosDbOptions>
{
    public CosmosDbOptionsValidator()
    {
        // Messages name the key, never the value: the connection string carries an account key.
        RuleFor(options => options.ConnectionString).NotEmpty().WithMessage("CosmosDb:ConnectionString is required.");
        RuleFor(options => options.DatabaseId).NotEmpty().WithMessage("CosmosDb:DatabaseId is required.");
        RuleFor(options => options.TranscriptContainerId).NotEmpty().WithMessage("CosmosDb:TranscriptContainerId is required.");
        RuleFor(options => options.PageSize).InclusiveBetween(1, 1000)
            .WithMessage("CosmosDb:PageSize must be between 1 and 1000.");
        // Cutover gate: a config still naming the pre-cutover container reads every transcript as empty.
        RuleFor(options => options.ContainerId).Null()
            .WithMessage($"{CosmosDbOptions.LegacyContainerKey} names the pre-cutover container, whose documents this application can no longer read. Remove the setting and set CosmosDb:TranscriptContainerId instead; see docs/operations/runbooks.md.");
    }
}
