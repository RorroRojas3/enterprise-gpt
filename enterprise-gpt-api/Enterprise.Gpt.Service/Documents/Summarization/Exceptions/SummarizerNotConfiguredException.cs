using System.Net;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.Documents.Summarization.Exceptions;

/// <summary>
/// Thrown when the pinned summarizer cannot be used: <c>Summarization:ModelId</c> names no active
/// <c>Core.Ref.Model</c> row, or the row is not filled in well enough to size a call; answered with 503.
/// </summary>
/// <remarks>
/// Shares <see cref="ProblemCodes.ProviderNotConfigured"/>: to a caller both mean this deployment cannot
/// serve the model. Checked per request too, since the row can change at runtime.
/// </remarks>
public class SummarizerNotConfiguredException(Guid modelId, string message)
    : ProblemException(HttpStatusCode.ServiceUnavailable, ProblemCodes.ProviderNotConfigured, message)
{
    /// <summary>For a configured id that resolves to no active catalog row.</summary>
    public SummarizerNotConfiguredException(Guid modelId)
        : this(modelId, $"Summarization:ModelId '{modelId}' names no active model in the catalog.")
    {
    }

    public Guid ModelId { get; } = modelId;
}
