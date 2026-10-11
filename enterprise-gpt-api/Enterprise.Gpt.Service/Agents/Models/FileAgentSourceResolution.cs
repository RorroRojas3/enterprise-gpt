namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>
/// Which of a conversation's files an instruction named, and which of its names cannot be acted on.
/// </summary>
/// <param name="Matched">The documents named, longest name first, each named exactly once.</param>
/// <param name="Ambiguous">Names shared by several live documents; the run asks rather than guesses.</param>
/// <param name="Unresolved">File names the instruction used that match nothing in the conversation.</param>
/// <param name="Available">Every file the run could work from, for the sentence a refusal ends on.</param>
public sealed record FileAgentSourceResolution(
    IReadOnlyList<FileAgentSourceMatch> Matched,
    IReadOnlyList<string> Ambiguous,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Available);
