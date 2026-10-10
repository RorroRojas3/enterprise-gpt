using System.Net;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.Documents.Summarization.Exceptions;

/// <summary>
/// Thrown when the pinned summarizer cannot be used: <c>Summarization:ModelId</c> names no active
/// <c>Core.Ref.Model</c> row, or the row is not filled in well enough to size a call; answered with 503.
/// </summary>
/// <remarks>
/// It shares <see cref="ProblemCodes.ProviderNotConfigured"/>: to a caller both mean this deployment
/// cannot serve the model. Raised at startup and again per request, since the row can change at runtime.
/// </remarks>
public class SummarizerNotConfiguredException : ProblemException
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
    /// A row can resolve and still be unusable, such as a seeded context window of zero; "names no
    /// active model" would send an operator looking for a row that is present.
    /// </remarks>
    public SummarizerNotConfiguredException(Guid modelId, string message)
        : base(HttpStatusCode.ServiceUnavailable, ProblemCodes.ProviderNotConfigured, message)
    {
        ModelId = modelId;
    }

    /// <summary>
    /// Gets the configured model id the failure concerns.
    /// </summary>
    public Guid ModelId { get; }
}
