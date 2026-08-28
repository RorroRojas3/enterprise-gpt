using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// The File Agent's catalog row as it stood when it was read.
/// </summary>
/// <param name="ModelId">The catalog row's identifier.</param>
/// <param name="ProviderId">The provider whose chat client serves it.</param>
/// <param name="DeploymentName">The provider-side identifier sent as <c>ChatOptions.ModelId</c>.</param>
public sealed record FileAgentModel(Guid ModelId, Guid ProviderId, string DeploymentName);

/// <summary>
/// Reads the pinned File Agent's catalog row.
/// </summary>
public interface IFileAgentModelResolver
{
    /// <summary>
    /// Reads the model row named by <c>FileAgent:ModelId</c>.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The row's current values.</returns>
    /// <remarks>
    /// Read per call rather than cached, so an administrator repointing the row's deployment takes
    /// effect on the next turn instead of at the next restart. A deactivated row is treated as
    /// absent, so retiring the agent's model stands the agent down rather than silently billing
    /// against a deployment an operator believes they withdrew.
    /// </remarks>
    /// <exception cref="FileAgentNotConfiguredException">
    /// The configured id names no active row, or names one on a provider that cannot carry a hosted
    /// code interpreter.
    /// </exception>
    Task<FileAgentModel> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the File Agent's catalog row out of <c>Core.Ref.Model</c>.
/// </summary>
/// <param name="ctx">The database context.</param>
/// <param name="options">The bound file-agent options.</param>
public sealed class FileAgentModelResolver(
    EnterpriseGptDbContext ctx,
    IOptions<FileAgentOptions> options) : IFileAgentModelResolver
{
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IOptions<FileAgentOptions> _options = options;

    /// <inheritdoc />
    public async Task<FileAgentModel> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var modelId = _options.Value.ModelId;

        var model = await _ctx.Models
            .AsNoTracking()
            .Where(x => x.Id == modelId && !x.DateDeactivated.HasValue)
            .Select(x => new FileAgentModel(x.Id, x.ProviderId, x.DeploymentName))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FileAgentNotConfiguredException(modelId);

        // Checked here rather than left to fail at the provider, because the failure it would cause
        // is silence: the Chat Completions bridge the other Azure provider uses drops a hosted tool
        // instead of rejecting it, so a misrouted agent would run and simply never produce a file.
        if (model.ProviderId != Providers.AzureOpenAI)
        {
            throw new FileAgentNotConfiguredException(
                modelId,
                $"FileAgent:ModelId '{modelId}' is on provider '{model.ProviderId}'. The File Agent runs a hosted "
                    + "code interpreter, which only the Azure OpenAI provider's Responses route carries.");
        }

        return model;
    }
}
