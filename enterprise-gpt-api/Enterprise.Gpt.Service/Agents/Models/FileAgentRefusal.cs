namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>A request the agent answers without opening a sandbox session at all.</summary>
/// <param name="Outcome">One of <see cref="FileAgentOutcomes"/>' three refusals.</param>
/// <param name="Text">The agent's own answer, which the assistant relays.</param>
/// <param name="SubStatus">The line the activity card shows, distinct per outcome.</param>
public sealed record FileAgentRefusal(string Outcome, string Text, string SubStatus);
