namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when <c>Summarization:ModelId</c> names no active <c>Core.Ref.Model</c> row. Raised at
/// startup by the summarizer bootstrapper, and again per request once a summarization route exists,
/// because the row can be deactivated after the application is already running.
/// </summary>
/// <remarks>
/// Derived from <see cref="Exception"/> rather than <see cref="InvalidOperationException"/> for the
/// reason <see cref="ProviderNotConfiguredException"/> gives: the handler maps the latter to 400,
/// which would report an operator's configuration gap as the caller's bad request.
/// </remarks>
/// <param name="modelId">The configured model id that could not be resolved.</param>
public class SummarizerNotConfiguredException(Guid modelId)
    : Exception($"Summarization:ModelId '{modelId}' names no active model in the catalog.")
{
    /// <summary>
    /// Gets the configured model id that could not be resolved.
    /// </summary>
    public Guid ModelId { get; } = modelId;
}
