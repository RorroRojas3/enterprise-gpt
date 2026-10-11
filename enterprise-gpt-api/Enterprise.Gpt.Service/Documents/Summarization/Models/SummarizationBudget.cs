namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// What one summarization stage is allowed to send, measured in tokens.
/// </summary>
/// <param name="PerCallTokens">
/// Room for one call's content after the output reservation, the framing and the safety fraction.
/// </param>
/// <param name="MapUnitTokens">
/// Below <paramref name="PerCallTokens"/>, because a unit is sized before the map framing is added.
/// </param>
public sealed record SummarizationBudget(long PerCallTokens, long MapUnitTokens);
