using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Documents.Summarization.Exceptions;
using Enterprise.Gpt.Service.Documents.Summarization.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents.Summarization;

/// <summary>
/// Reads the pinned summarizer's catalog row.
/// </summary>
public interface ISummarizerModelResolver
{
    /// <summary>
    /// Reads the model row named by <c>Summarization:ModelId</c>.
    /// </summary>
    /// <remarks>
    /// Read per call so catalog edits apply on the next summarization. A deactivated row counts as absent,
    /// so retiring the summarizer stops billing against it.
    /// </remarks>
    /// <exception cref="SummarizerNotConfiguredException">The configured id names no active row.</exception>
    Task<SummarizerModel> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SummarizerModelResolver(
    EnterpriseGptDbContext ctx,
    IOptions<SummarizationOptions> options) : ISummarizerModelResolver
{
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IOptions<SummarizationOptions> _options = options;

    /// <inheritdoc />
    public async Task<SummarizerModel> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var modelId = _options.Value.ModelId;

        return await _ctx.Models
            .AsNoTracking()
            .Where(x => x.Id == modelId && !x.DateDeactivated.HasValue)
            .Select(x => new SummarizerModel(
                x.Id,
                x.ProviderId,
                x.DeploymentName,
                x.ContextWindowSize,
                x.MaxOutputTokens,
                x.InputPricePerMillionTokens,
                x.OutputPricePerMillionTokens))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new SummarizerNotConfiguredException(modelId);
    }
}
