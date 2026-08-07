namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when a model names a provider this deployment has no chat client for — either an id
/// absent from the provider-to-service-key map, or a provider whose client is switched off by
/// configuration. Mapped to HTTP 503 by the global exception handler: the request is well formed
/// and the model exists, so this is an operator condition rather than a client mistake.
/// </summary>
/// <remarks>
/// Derived from <see cref="Exception"/> rather than <see cref="InvalidOperationException"/> on
/// purpose — the handler maps the latter to 400, which would misreport a configuration gap as a bad
/// request.
/// </remarks>
/// <param name="providerId">The provider that could not be resolved.</param>
public class ProviderNotConfiguredException(Guid providerId)
    : Exception($"No chat client is configured for provider '{providerId}'.")
{
    /// <summary>
    /// Gets the identifier of the provider that could not be resolved.
    /// </summary>
    public Guid ProviderId { get; } = providerId;
}
