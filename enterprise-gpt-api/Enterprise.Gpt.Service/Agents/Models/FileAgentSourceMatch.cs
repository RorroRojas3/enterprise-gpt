namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>One document an instruction named, before its bytes are read.</summary>
/// <param name="Extension">The lower-cased extension including its dot, which the matrix is keyed on.</param>
public sealed record FileAgentSourceMatch(Guid Id, string Name, string Extension);
