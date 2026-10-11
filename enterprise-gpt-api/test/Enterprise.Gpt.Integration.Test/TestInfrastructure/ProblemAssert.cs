using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

public static class ProblemAssert
{
    /// <remarks>
    /// Always reads <see cref="HttpValidationProblemDetails"/>, a <see cref="ProblemDetails"/> whose
    /// <see cref="HttpValidationProblemDetails.Errors"/> a non-validation problem leaves empty.
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

        // Extension values arrive boxed as JsonElement through [JsonExtensionData].
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

    public static async Task<ProblemResponse> ReadValidationAsync(HttpResponseMessage response)
    {
        var problem = await ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.NotEmpty(problem.Errors);
        return problem;
    }
}
