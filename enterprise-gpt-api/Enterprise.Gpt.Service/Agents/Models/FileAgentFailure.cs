namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>A run that ended without a file, and what each audience is told about it.</summary>
/// <param name="Outcome">One of <see cref="FileAgentOutcomes"/>' four failures.</param>
/// <param name="Message">What the assistant is handed, to relay in its own words.</param>
/// <param name="SubStatus">The line the activity card shows.</param>
public sealed record FileAgentFailure(string Outcome, string Message, string SubStatus);
