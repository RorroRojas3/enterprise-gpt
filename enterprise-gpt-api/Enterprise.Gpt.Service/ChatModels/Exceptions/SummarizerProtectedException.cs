using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.ChatModels.Exceptions;

/// <summary>
/// Thrown when an administrator tries to retire the catalog row named by <c>Summarization:ModelId</c>.
/// </summary>
/// <remarks>
/// No domain problem type: a <c>DELETE</c> has no body to key an <c>errors</c> entry against, so the
/// message reaches the caller as <c>detail</c>.
/// </remarks>
public class SummarizerProtectedException(Guid modelId)
    : ConflictException($"Model '{modelId}' is the configured document summarizer and cannot be retired. Point Summarization:ModelId at another model first.")
{
    public Guid ModelId { get; } = modelId;
}
