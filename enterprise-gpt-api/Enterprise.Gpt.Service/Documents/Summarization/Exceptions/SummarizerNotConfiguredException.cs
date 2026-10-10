namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when the pinned summarizer cannot be used: <c>Summarization:ModelId</c> names no active
/// <c>Core.Ref.Model</c> row, or the row it names is not filled in well enough to size a call.
/// Raised at startup by the summarizer bootstrapper, and again per request, because the row can be
/// deactivated or edited after the application is already running.
/// </summary>
/// <remarks>
/// Derived from <see cref="Exception"/> rather than <see cref="InvalidOperationException"/> for the
/// reason <see cref="ProviderNotConfiguredException"/> gives: the handler maps the latter to 400,
/// which would report an operator's configuration gap as the caller's bad request.
/// </remarks>
public class SummarizerNotConfiguredException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SummarizerNotConfiguredException"/> class for a
    /// configured id that resolves to no active catalog row.
    /// </summary>
    /// <param name="modelId">The configured model id that could not be resolved.</param>
    public SummarizerNotConfiguredException(Guid modelId)
        : this(modelId, $"Summarization:ModelId '{modelId}' names no active model in the catalog.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SummarizerNotConfiguredException"/> class with a
    /// message describing how the row is unusable.
    /// </summary>
    /// <param name="modelId">The configured model id.</param>
    /// <param name="message">What is wrong with the row.</param>
    /// <remarks>
    /// A row can resolve and still be unusable — a context window of zero is the seeded default for
    /// a catalog row nobody has filled in. Reporting that as "names no active model" would send an
    /// operator looking for a missing row that is in fact present.
    /// </remarks>
    public SummarizerNotConfiguredException(Guid modelId, string message)
        : base(message)
    {
        ModelId = modelId;
    }

    /// <summary>
    /// Gets the configured model id the failure concerns.
    /// </summary>
    public Guid ModelId { get; }
}
