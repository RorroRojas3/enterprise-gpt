using Enterprise.Gpt.Api.Agents;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Prompts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Enterprise.Gpt.Api.Startup;

/// <summary>
/// Refuses to start against a File Agent that could not run: a <c>FileAgent:ModelId</c> naming no
/// usable catalog row, a missing chat client, an instruction template left out of the build, or a
/// skills directory that did not ship.
/// </summary>
/// <remarks>
/// <para>
/// Runs unconditionally, whatever <c>FileAgent:Enabled</c> says: a deployment whose agent is
/// misconfigured should say so when it is deployed, not on the first request after somebody
/// switches the feature on months later.
/// </para>
/// <para>
/// Called inline from <c>Program.cs</c> beside the other two bootstrappers rather than registered as
/// an <see cref="IHostedService"/>, and for the same reason: it needs a live backing store, and
/// <c>WebApplicationFactory</c> starts the host before the integration fixture creates the schema.
/// </para>
/// </remarks>
public static class FileAgentBootstrapper
{
    /// <summary>
    /// Resolves the pinned model, verifies its client and its keyed sandbox client exist, and forces
    /// the prompt and skill content to be read off disk.
    /// </summary>
    /// <param name="services">A scoped service provider.</param>
    /// <param name="skillsRoot">The directory the deployed skills were expected at.</param>
    /// <param name="logger">The logger that reports the resolved deployment.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="Service.Exceptions.FileAgentNotConfiguredException">
    /// The configured id names no active model row, or one on a provider that cannot carry a hosted
    /// code interpreter.
    /// </exception>
    /// <exception cref="Service.Exceptions.ProviderNotConfiguredException">
    /// The row's provider has no chat client registered in this deployment.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">The deployed skills are missing.</exception>
    public static async Task ValidateAsync(
        IServiceProvider services, string skillsRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        var model = await services.GetRequiredService<IFileAgentModelResolver>().ResolveAsync(cancellationToken);

        // Both discarded for their throws: the agent runs on its own keyed client, and the resolver
        // above only proved the row points at the provider that client belongs to.
        _ = services.GetRequiredService<IChatClientResolver>().Resolve(model.ProviderId);
        _ = services.GetRequiredKeyedService<IChatClient>(ChatClientKeys.FileAgent);

        // Forces the instruction template's static initialiser, so a template left out of the build
        // output fails the deploy rather than the first user request.
        _ = ConversationPrompts.BuildFileAgentPrompt([]);

        // Same reason, and it fails the same way: the run's pre-flight refuses a conversion from this
        // file, and a deployment without it would offer every pair instead of the confirmed ones.
        var matrix = services.GetRequiredService<IConversionMatrix>();

        var skills = FileAgentSkills.Discover(skillsRoot);

        logger.LogInformation(
            "The File Agent will use model {ModelId} ({DeploymentName}) on provider {ProviderId}, with {SkillCount} skill(s) "
                + "and a conversion matrix of {CellCount} cell(s).",
            model.ModelId,
            model.DeploymentName,
            model.ProviderId,
            skills.Count,
            matrix.Cells.Count);
    }
}
