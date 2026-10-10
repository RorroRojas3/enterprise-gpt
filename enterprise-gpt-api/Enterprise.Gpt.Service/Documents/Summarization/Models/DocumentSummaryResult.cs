namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// One document's summary, and whether producing it cost anything.
/// </summary>
/// <param name="Text">The summary text.</param>
/// <param name="FromCache">
/// <see langword="true"/> when the stored summary was reused, in which case no model call was made
/// and no usage was billed.
/// </param>
/// <param name="ModelCallCount">Model calls this request made: <c>0</c> on a cache hit.</param>
public sealed record DocumentSummaryResult(string Text, bool FromCache, int ModelCallCount);
