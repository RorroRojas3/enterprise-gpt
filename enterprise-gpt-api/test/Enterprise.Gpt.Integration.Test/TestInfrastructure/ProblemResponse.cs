using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// The RFC 9457 fields a test asserts on, projected off the wire so callers do not have to dig
/// <c>traceId</c> out of the extension data bag.
/// </summary>
/// <param name="Type">The problem type URI.</param>
/// <param name="Title">The short, occurrence-independent summary.</param>
/// <param name="Status">The status code echoed in the body.</param>
/// <param name="Detail">The occurrence-specific explanation.</param>
/// <param name="Instance">The path the problem occurred on.</param>
/// <param name="TraceId">The correlation id carried in the <c>traceId</c> extension.</param>
/// <param name="Errors">The per-property validation messages; empty for non-validation problems.</param>
/// <param name="Extensions">
/// The raw extension members as they arrived on the wire, for asserting the problem-specific fields
/// (<c>serverName</c>, <c>permissions</c>, <c>maxBytes</c>).
/// </param>
public sealed record ProblemResponse(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Instance,
    string? TraceId,
    IReadOnlyDictionary<string, string[]> Errors,
    IReadOnlyDictionary<string, JsonElement> Extensions);
