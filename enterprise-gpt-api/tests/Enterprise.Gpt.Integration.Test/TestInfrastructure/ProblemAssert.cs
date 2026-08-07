using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

/// <summary>
/// Reads and validates the problem details body every failing endpoint in this API returns.
/// </summary>
public static class ProblemAssert
{
    /// <summary>
    /// Asserts the response is an RFC 9457 problem with the expected status, and returns it.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <param name="expectedStatusCode">The status code the endpoint is expected to return.</param>
    /// <returns>The parsed problem.</returns>
    /// <remarks>
    /// Deserializes as <see cref="HttpValidationProblemDetails"/> unconditionally: it derives from
    /// <see cref="ProblemDetails"/>, so a non-validation problem simply leaves
    /// <see cref="HttpValidationProblemDetails.Errors"/> at the empty dictionary its constructor
    /// allocates, and one code path covers both shapes.
    /// </remarks>
    public static async Task<ProblemResponse> ReadAsync(
        HttpResponseMessage response, HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal((int)expectedStatusCode, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Type));
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));

        // Extension values arrive boxed as JsonElement because ProblemDetails collects them through
        // [JsonExtensionData] over IDictionary<string, object?>.
        var traceId = problem.Extensions.TryGetValue("traceId", out var value)
            && value is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : null;
        Assert.False(string.IsNullOrWhiteSpace(traceId));

        var extensions = problem.Extensions
            .Where(entry => entry.Value is JsonElement)
            .ToDictionary(entry => entry.Key, entry => (JsonElement)entry.Value!, StringComparer.Ordinal);

        return new ProblemResponse(
            problem.Type,
            problem.Title,
            problem.Status,
            problem.Detail,
            problem.Instance,
            traceId,
            problem.Errors.AsReadOnly(),
            extensions);
    }

    /// <summary>
    /// Asserts the response is a 400 validation problem carrying at least one field error.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The parsed problem.</returns>
    public static async Task<ProblemResponse> ReadValidationAsync(HttpResponseMessage response)
    {
        var problem = await ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.NotEmpty(problem.Errors);
        return problem;
    }
}
