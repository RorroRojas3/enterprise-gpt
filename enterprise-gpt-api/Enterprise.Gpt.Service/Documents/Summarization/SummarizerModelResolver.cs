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
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The row's current values.</returns>
    /// <remarks>
    /// Read per call rather than cached, so an administrator editing the row's window, deployment
    /// name or price takes effect on the next summarization instead of at the next restart. A
    /// deactivated row is treated as absent — retiring the summarizer must stop summarization, not
    /// silently keep billing against a deployment an operator believes they withdrew.
    /// </remarks>
    /// <exception cref="SummarizerNotConfiguredException">
    /// The configured id names no row, or names one that has been deactivated.
    /// </exception>
    Task<SummarizerModel> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the summarizer's catalog row out of <c>Core.Ref.Model</c>.
/// </summary>
/// <param name="ctx">The database context.</param>
/// <param name="options">The bound summarization options.</param>
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
