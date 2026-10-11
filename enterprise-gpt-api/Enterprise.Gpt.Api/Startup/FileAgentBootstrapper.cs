using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Agents.Exceptions;
using Enterprise.Gpt.Service.Conversations.Chat;
using Enterprise.Gpt.Service.Conversations.Chat.Exceptions;
using Enterprise.Gpt.Service.Prompts;
using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Api.Startup;

/// <summary>
/// Refuses to start against a File Agent that could not run: an unusable model, a missing chat client,
/// or a prompt template or skills directory that did not ship.
/// </summary>
/// <remarks>
/// Runs whatever <c>FileAgent:Enabled</c> says, so misconfiguration fails the deploy. Not an
/// <see cref="IHostedService"/>, for the same reason as <see cref="SummarizerBootstrapper"/>.
/// </remarks>
public static partial class FileAgentBootstrapper
{
    #region Public static methods

    /// <summary>
    /// Resolves the pinned model from a scoped provider, verifies its clients exist, and forces the
    /// prompt and skill content to be read off disk.
    /// </summary>
    /// <exception cref="FileAgentNotConfiguredException">
    /// No active model row, or one on a provider without a hosted code interpreter.
    /// </exception>
    /// <exception cref="ProviderNotConfiguredException">The row's provider has no chat client.</exception>
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

        LogFileAgentModel(
            logger, model.ModelId, model.DeploymentName, model.ProviderId, skills.Count, matrix.Cells.Count);
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "The File Agent will use model {ModelId} ({DeploymentName}) on provider {ProviderId}, with {SkillCount} skill(s) and a conversion matrix of {CellCount} cell(s).")]
    private static partial void LogFileAgentModel(
        ILogger logger, Guid modelId, string deploymentName, Guid providerId, int skillCount, int cellCount);

    #endregion
}
