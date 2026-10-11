namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// The summarizer's catalog row as it stood when it was read.
/// </summary>
/// <remarks>
/// A <paramref name="ContextWindowSize"/> of zero or less means the row was never filled in: refuse rather
/// than read it as unbounded.
/// </remarks>
public sealed record SummarizerModel(
    Guid ModelId,
    Guid ProviderId,
    string DeploymentName,
    decimal ContextWindowSize,
    decimal MaxOutputTokens,
    decimal? InputPricePerMillionTokens,
    decimal? OutputPricePerMillionTokens);
