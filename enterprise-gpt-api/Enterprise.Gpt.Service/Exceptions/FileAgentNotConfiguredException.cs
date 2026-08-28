namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when the File Agent cannot be composed: <c>FileAgent:ModelId</c> names no active
/// <c>Core.Ref.Model</c> row, or names one on a provider that cannot carry a hosted code
/// interpreter. Raised at startup by the file-agent bootstrapper, and again per turn, because the
/// row can be deactivated or repointed after the application is already running.
/// </summary>
/// <remarks>
/// Derived from <see cref="Exception"/> rather than <see cref="InvalidOperationException"/> for the
/// reason <see cref="ProviderNotConfiguredException"/> gives: the handler maps the latter to 400,
/// which would report an operator's configuration gap as the caller's bad request.
/// </remarks>
public class FileAgentNotConfiguredException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileAgentNotConfiguredException"/> class for a
    /// configured id that resolves to no active catalog row.
    /// </summary>
    /// <param name="modelId">The configured model id that could not be resolved.</param>
    public FileAgentNotConfiguredException(Guid modelId)
        : this(modelId, $"FileAgent:ModelId '{modelId}' names no active model in the catalog.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileAgentNotConfiguredException"/> class with a
    /// message describing how the row is unusable.
    /// </summary>
    /// <param name="modelId">The configured model id.</param>
    /// <param name="message">What is wrong with the row.</param>
    public FileAgentNotConfiguredException(Guid modelId, string message)
        : base(message)
    {
        ModelId = modelId;
    }

    /// <summary>
    /// Gets the configured model id the failure concerns.
    /// </summary>
    public Guid ModelId { get; }
}
