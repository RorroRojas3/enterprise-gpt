namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when an administrator tries to retire the catalog row named by
/// <c>Summarization:ModelId</c>. Mapped to HTTP 409 by the global exception handler.
/// </summary>
/// <remarks>
/// <para>
/// 409 rather than 400: the request is well formed and the model exists. The refusal is about a
/// relationship between the row and this deployment's configuration, which is a conflict rather
/// than a mistake in what was sent.
/// </para>
/// <para>
/// It carries no domain problem type on purpose. A <c>DELETE</c> has no request body, so there is
/// no form field a client could project an <c>errors</c> entry onto, and the whole value of this
/// refusal is the sentence telling an operator which setting to change — which reaches the caller
/// through <c>Detail</c>. A caller that only reads the status still learns the row is protected.
/// </para>
/// </remarks>
/// <param name="modelId">The protected model's identifier.</param>
public class SummarizerProtectedException(Guid modelId)
    : Exception($"Model '{modelId}' is the configured document summarizer and cannot be retired. Point Summarization:ModelId at another model first.")
{
    /// <summary>
    /// Gets the identifier of the model that is protected.
    /// </summary>
    public Guid ModelId { get; } = modelId;
}
