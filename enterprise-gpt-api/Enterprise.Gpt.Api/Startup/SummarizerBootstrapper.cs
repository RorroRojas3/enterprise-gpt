using Enterprise.Gpt.Service.Conversations.Chat;
using Enterprise.Gpt.Service.Conversations.Chat.Exceptions;
using Enterprise.Gpt.Service.Documents.Summarization;
using Enterprise.Gpt.Service.Documents.Summarization.Exceptions;
using Enterprise.Gpt.Service.Prompts;

namespace Enterprise.Gpt.Api.Startup;

/// <summary>
/// Refuses to start against a <c>Summarization:ModelId</c> that names no usable catalog row.
/// </summary>
/// <remarks>
/// Runs unconditionally, so misconfiguration fails the deploy. Not an <see cref="IHostedService"/>:
/// <c>WebApplicationFactory</c> starts the host before the integration fixture creates the schema.
/// <c>ContextWindowSize</c> is checked per request instead, as an administrator can change it after start.
/// </remarks>
public static partial class SummarizerBootstrapper
{
    #region Public static methods

    /// <summary>
    /// Resolves the configured summarizer from a scoped provider and verifies its provider has a chat client.
    /// </summary>
    /// <exception cref="SummarizerNotConfiguredException">The id names no active model row.</exception>
    /// <exception cref="ProviderNotConfiguredException">The row's provider has no chat client.</exception>
    public static async Task ValidateAsync(
        IServiceProvider services, ILogger logger, CancellationToken cancellationToken = default)
    {
        var summarizer = await services.GetRequiredService<ISummarizerModelResolver>()
            .ResolveAsync(cancellationToken);

        // Called for its throw, so an unregistered or switched-off provider fails the deploy.
        _ = services.GetRequiredService<IChatClientResolver>().Resolve(summarizer.ProviderId);

        // Called for its throw: forces the static initialiser that reads the markdown templates, so a
        // missing template fails the deploy. PromptVersion is a const and would not trigger it.
        _ = SummarizationPrompts.BuildReduce(["probe"]);

        LogSummarizerModel(logger, summarizer.ModelId, summarizer.DeploymentName, summarizer.ProviderId);
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Document summarization will use model {ModelId} ({DeploymentName}) on provider {ProviderId}.")]
    private static partial void LogSummarizerModel(
        ILogger logger, Guid modelId, string deploymentName, Guid providerId);

    #endregion
}
