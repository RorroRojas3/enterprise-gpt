using System.Net;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.Conversations.Chat.Exceptions;

/// <summary>
/// Thrown when a model names a provider this deployment has no chat client for, either unmapped or
/// switched off by configuration; answered with 503.
/// </summary>
/// <param name="providerId">The provider that could not be resolved.</param>
public class ProviderNotConfiguredException(Guid providerId)
    : ProblemException(
        HttpStatusCode.ServiceUnavailable,
        ProblemCodes.ProviderNotConfigured,
        $"No chat client is configured for provider '{providerId}'.")
{
    /// <summary>
    /// Gets the identifier of the provider that could not be resolved.
    /// </summary>
    public Guid ProviderId { get; } = providerId;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> Extensions =>
        new Dictionary<string, object?> { ["providerId"] = ProviderId };
}
