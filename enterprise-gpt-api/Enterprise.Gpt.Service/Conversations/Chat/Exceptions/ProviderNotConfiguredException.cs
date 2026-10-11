using System.Net;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.Conversations.Chat.Exceptions;

/// <summary>
/// Thrown when a model names a provider this deployment has no chat client for, either unmapped or
/// switched off by configuration; answered with 503.
/// </summary>
public class ProviderNotConfiguredException(Guid providerId)
    : ProblemException(
        HttpStatusCode.ServiceUnavailable,
        ProblemCodes.ProviderNotConfigured,
        $"No chat client is configured for provider '{providerId}'.")
{
    public Guid ProviderId { get; } = providerId;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> Extensions =>
        new Dictionary<string, object?> { ["providerId"] = ProviderId };
}
