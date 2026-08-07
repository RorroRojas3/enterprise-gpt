namespace Enterprise.Gpt.Api.Problems;

/// <summary>
/// A stable, application-specific RFC 9457 problem type: the <c>type</c> URI clients branch on and
/// the occurrence-independent <c>title</c> that accompanies it.
/// </summary>
/// <param name="Type">The problem type URI written to <c>type</c>.</param>
/// <param name="Title">The short, occurrence-independent summary written to <c>title</c>.</param>
internal readonly record struct ProblemType(string Type, string Title);
