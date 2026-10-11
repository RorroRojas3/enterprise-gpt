using System.Text.Json;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// The RFC 9457 fields a test asserts on, with <c>traceId</c> lifted out of the extension data.
/// </summary>
public sealed record ProblemResponse(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Instance,
    string? TraceId,
    IReadOnlyDictionary<string, string[]> Errors,
    IReadOnlyDictionary<string, JsonElement> Extensions);
