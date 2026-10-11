using System.Text.Json;
using Enterprise.Gpt.Api.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
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
    internal static IProblemDetailsService CreateProblemDetailsService()
    {
        return new ServiceCollection()
            .AddLogging()
            .AddEnterpriseGptProblemDetails()
            .BuildServiceProvider()
            .GetRequiredService<IProblemDetailsService>();
    }

    internal static DefaultHttpContext CreateHttpContext(string path = "/api/test")
    {
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }
        };
        httpContext.Request.Path = path;
        return httpContext;
    }

    internal static void MarkResponseStarted(HttpContext httpContext)
    {
        httpContext.Features.Set<IHttpResponseFeature>(new StartedResponseFeature(httpContext.Response.Body));
    }

    /// <summary>
    /// Always reads <see cref="HttpValidationProblemDetails"/>: a non-validation problem just leaves
    /// <c>Errors</c> empty, so one path covers both shapes.
    /// </summary>
    internal static async Task<HttpValidationProblemDetails> ReadProblemAsync(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<HttpValidationProblemDetails>(
            httpContext.Response.Body,
            JsonSerializerOptions.Web,
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        return problem;
    }

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
