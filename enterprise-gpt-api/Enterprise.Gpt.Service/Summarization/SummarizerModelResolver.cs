using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Summarization;

/// <summary>
/// The summarizer's catalog row as it stood when it was read, carrying every fact a summarization
/// call needs about it.
/// </summary>
/// <param name="ModelId">The catalog row's identifier.</param>
/// <param name="ProviderId">The provider whose chat client serves it.</param>
/// <param name="DeploymentName">The provider-side identifier sent as <c>ChatOptions.ModelId</c>.</param>
/// <param name="ContextWindowSize">
/// The declared context window. A value of zero or less means the row was never filled in; callers
/// deciding whether a document fits must refuse rather than read it as unbounded.
/// </param>
/// <param name="MaxOutputTokens">The declared per-call output cap.</param>
/// <param name="InputPricePerMillionTokens">USD per million input tokens, or null when unpriced.</param>
/// <param name="OutputPricePerMillionTokens">USD per million output tokens, or null when unpriced.</param>
public sealed record SummarizerModel(
    Guid ModelId,
    Guid ProviderId,
    string DeploymentName,
    decimal ContextWindowSize,
    decimal MaxOutputTokens,
    decimal? InputPricePerMillionTokens,
    decimal? OutputPricePerMillionTokens);

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
