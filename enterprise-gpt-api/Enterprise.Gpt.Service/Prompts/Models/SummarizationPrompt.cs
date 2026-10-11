namespace Enterprise.Gpt.Service.Prompts.Models;

/// <summary>One summarization call's prompt, split between the instructions and the user message.</summary>
/// <param name="Instructions">The framing; constant per stage, so its tokens are budgeted as overhead.</param>
/// <param name="UserMessage">The delimited content, which is what the budget measures.</param>
public sealed record SummarizationPrompt(string Instructions, string UserMessage);
