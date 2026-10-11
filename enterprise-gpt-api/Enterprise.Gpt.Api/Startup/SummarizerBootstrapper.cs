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
/// <para>
/// Runs unconditionally, whatever governs whether summarization is offered to users: a deployment
/// whose summarizer is misconfigured should say so when it is deployed, not on the first request
/// after somebody switches the feature on months later.
/// </para>
/// <para>
/// Run by <see cref="StartupBootstrapper"/> after the schema and Cosmos provisioning rather than
/// registered as an <see cref="IHostedService"/>, because it needs a live backing
/// store. <c>WebApplicationFactory</c> starts the host the first time its service provider is
/// touched, which the integration fixture does <em>before</em> it creates the schema, so a hosted
/// service reading <c>Core.Ref.Model</c> would query a database that has no tables yet.
/// </para>
/// <para>
/// It deliberately does not check <c>ContextWindowSize</c>. A window of zero is a real failure, but
/// an administrator can introduce one after start, so that check belongs on the request path where
/// it can see the row as it stands.
/// </para>
/// </remarks>
public static partial class SummarizerBootstrapper
{
    #region Public static methods

    /// <summary>
    /// Resolves the configured summarizer and verifies a chat client exists for its provider.
    /// </summary>
    /// <param name="services">A scoped service provider.</param>
    /// <param name="logger">The logger that reports the resolved deployment.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="SummarizerNotConfiguredException">
    /// The configured id names no active model row.
    /// </exception>
    /// <exception cref="ProviderNotConfiguredException">
    /// The row's provider has no chat client registered in this deployment.
    /// </exception>
    public static async Task ValidateAsync(
        IServiceProvider services, ILogger logger, CancellationToken cancellationToken = default)
    {
        var summarizer = await services.GetRequiredService<ISummarizerModelResolver>()
            .ResolveAsync(cancellationToken);

        // The same failure ChatClientResolver would raise on the first summarization call, raised
        // here instead so an unregistered or switched-off provider is a deploy-time condition.
        // Discarded on purpose: the call is made for its throw, not its result.
        _ = services.GetRequiredService<IChatClientResolver>().Resolve(summarizer.ProviderId);

        // Forces SummarizationPrompts' static initialiser, which reads the three markdown templates
        // off disk. A template missing from the build output would otherwise surface as a
        // TypeInitializationException on the first summarization request — reported to that user as
        // a generic "try again", which would never come true. Also discarded for its throw alone;
        // PromptVersion is a const, so naming it would not touch the initialiser.
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
