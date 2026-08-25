using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Summarization;
using Microsoft.Extensions.DependencyInjection;

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
/// Called inline from <c>Program.cs</c> beside <see cref="CosmosBootstrapper"/> rather than
/// registered as an <see cref="IHostedService"/>, and for the same reason: it needs a live backing
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
public static class SummarizerBootstrapper
{
    /// <summary>
    /// Resolves the configured summarizer and verifies a chat client exists for its provider.
    /// </summary>
    /// <param name="services">A scoped service provider.</param>
    /// <param name="logger">The logger that reports the resolved deployment.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="Service.Exceptions.SummarizerNotConfiguredException">
    /// The configured id names no active model row.
    /// </exception>
    /// <exception cref="Service.Exceptions.ProviderNotConfiguredException">
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

        logger.LogInformation(
            "Document summarization will use model {ModelId} ({DeploymentName}) on provider {ProviderId}.",
            summarizer.ModelId,
            summarizer.DeploymentName,
            summarizer.ProviderId);
    }
}
