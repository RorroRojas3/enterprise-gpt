namespace Enterprise.Gpt.Service.Prompts.Models;

/// <summary>
/// One summarization call's prompt, split across the two places it is carried.
/// </summary>
/// <param name="Instructions">
/// The framing, for <c>ChatOptions.Instructions</c>. Constant per stage, so its token cost can be
/// counted once and charged to the budget as overhead.
/// </param>
/// <param name="UserMessage">
/// The delimited content, for the single user message. This is what the budget measures.
/// </param>
public sealed record SummarizationPrompt(string Instructions, string UserMessage);
