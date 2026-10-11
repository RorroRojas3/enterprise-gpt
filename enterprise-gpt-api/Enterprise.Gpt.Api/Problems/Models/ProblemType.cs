namespace Enterprise.Gpt.Api.Problems.Models;

/// <summary>
/// A stable, application-specific RFC 9457 problem type: the <c>type</c> URI clients branch on and
/// the occurrence-independent <c>title</c> that accompanies it.
/// </summary>
internal readonly record struct ProblemType(string Type, string Title);
