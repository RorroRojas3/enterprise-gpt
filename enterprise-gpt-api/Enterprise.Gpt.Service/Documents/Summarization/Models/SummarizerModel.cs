namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

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
