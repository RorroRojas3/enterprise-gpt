namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>A request the agent answers without opening a sandbox session at all.</summary>
/// <param name="Outcome">One of <see cref="FileAgentOutcomes"/>' three refusals.</param>
/// <param name="Text">The agent's own answer, which the assistant relays.</param>
/// <param name="SubStatus">
/// The line the activity card shows. Distinct per outcome, because a reader has to be able to tell an
/// unsupported conversion from a name that matched nothing.
/// </param>
public sealed record FileAgentRefusal(string Outcome, string Text, string SubStatus);
