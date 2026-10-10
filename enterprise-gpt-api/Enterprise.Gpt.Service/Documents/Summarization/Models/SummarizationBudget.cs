using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// What one summarization stage is allowed to send, measured in tokens.
/// </summary>
/// <param name="PerCallTokens">
/// The most a single call's variable content — the document, or the partial summaries being
/// reduced — may occupy, after the output reservation, the framing and the safety fraction.
/// </param>
/// <param name="MapUnitTokens">
/// The most one map unit may occupy. Strictly below <paramref name="PerCallTokens"/>, because a
/// unit is sized before the map prompt's own framing is added to it.
/// </param>
public sealed record SummarizationBudget(long PerCallTokens, long MapUnitTokens);
