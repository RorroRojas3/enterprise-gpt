using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Enterprise.Gpt.Api.Problems;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Shared plumbing for the exception-handler tests: a context whose body can be read back, the
/// production problem details service, and the deserialization of what the handler wrote.
/// </summary>
internal static class ProblemDetailsTestHelpers
{
    /// <summary>
    /// Builds the real <see cref="IProblemDetailsService"/> from the production registration, so the
    /// tests exercise <c>CustomizeProblemDetails</c> rather than a stub.
    /// </summary>
    /// <returns>The configured service.</returns>
    internal static IProblemDetailsService CreateProblemDetailsService()
    {
        return new ServiceCollection()
            .AddLogging()
            .AddEnterpriseProblemDetails()
            .BuildServiceProvider()
            .GetRequiredService<IProblemDetailsService>();
    }

    /// <summary>
    /// Creates a context with a readable response body and a request path, so <c>instance</c> has
    /// something to report.
    /// </summary>
    /// <param name="path">The request path to report as the problem's instance.</param>
    /// <returns>The context.</returns>
    internal static DefaultHttpContext CreateHttpContext(string path = "/api/test")
    {
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }
        };
        httpContext.Request.Path = path;
        return httpContext;
    }

    /// <summary>
    /// Marks the response as already started, so the handlers' short-circuit can be exercised.
    /// </summary>
    /// <param name="httpContext">The context to mark.</param>
    internal static void MarkResponseStarted(HttpContext httpContext)
    {
        httpContext.Features.Set<IHttpResponseFeature>(new StartedResponseFeature(httpContext.Response.Body));
    }

    /// <summary>
    /// Reads back what the handler wrote to the response body.
    /// </summary>
    /// <param name="httpContext">The context whose body to read.</param>
    /// <returns>The deserialized problem.</returns>
    /// <remarks>
    /// Always deserializes as <see cref="HttpValidationProblemDetails"/>: it derives from
    /// <see cref="ProblemDetails"/>, so a non-validation problem leaves <c>Errors</c> empty and one
    /// path covers both shapes.
    /// </remarks>
    internal static async Task<HttpValidationProblemDetails> ReadProblemAsync(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<HttpValidationProblemDetails>(
            httpContext.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        return problem;
    }

    /// <summary>
    /// Reads a string-valued extension member off a deserialized problem.
    /// </summary>
    /// <param name="problem">The problem to read from.</param>
    /// <param name="key">The extension key.</param>
    /// <returns>The value, or <see langword="null"/> when absent or not a string.</returns>
    internal static string? ReadExtension(HttpValidationProblemDetails problem, string key)
    {
        return problem.Extensions.TryGetValue(key, out var value)
            && value is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : null;
    }

    private sealed class StartedResponseFeature(Stream body) : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = body;

        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
