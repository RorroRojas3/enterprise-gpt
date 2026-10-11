namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// One document's summary; a cache hit made no model call and billed nothing.
/// </summary>
public sealed record DocumentSummaryResult(string Text, bool FromCache, int ModelCallCount);
