using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Agents.Exceptions;
using Enterprise.Gpt.Service.Agents.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// Reads the pinned File Agent's catalog row.
/// </summary>
public interface IFileAgentModelResolver
{
    /// <summary>Reads the model row named by <c>FileAgent:ModelId</c>.</summary>
    /// <remarks>
    /// Read per call so a repointed deployment applies next turn; a deactivated row stands the agent down.
    /// </remarks>
    /// <exception cref="FileAgentNotConfiguredException">
    /// No active row has the configured id, or its provider cannot host a code interpreter.
    /// </exception>
    Task<FileAgentModel> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
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

        // Checked here because the provider would fail silently: the other Azure provider's Chat Completions
        // bridge drops a hosted tool instead of rejecting it, so the agent would never produce a file.
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
