using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.ChatModels.Exceptions;

/// <summary>
/// Thrown when an administrator tries to retire the catalog row named by <c>Summarization:ModelId</c>.
/// </summary>
/// <remarks>
/// It carries no domain problem type: a <c>DELETE</c> has no body for a client to key an <c>errors</c>
/// entry against, and the message naming the setting to change reaches the caller as <c>detail</c>.
/// </remarks>
/// <param name="modelId">The protected model's identifier.</param>
public class SummarizerProtectedException(Guid modelId)
    : ConflictException($"Model '{modelId}' is the configured document summarizer and cannot be retired. Point Summarization:ModelId at another model first.")
{
    /// <summary>
    /// Gets the identifier of the model that is protected.
    /// </summary>
    public Guid ModelId { get; } = modelId;
}
