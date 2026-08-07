using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Api.ExceptionHandlers;
using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Exceptions;
using Xunit;
using static Enterprise.Gpt.Unit.Test.TestInfrastructure.ProblemDetailsTestHelpers;

namespace Enterprise.Gpt.Unit.Test.ExceptionHandlers;

public class GlobalExceptionHandlerTests
{
    // Built from the production registration, so CustomizeProblemDetails — and therefore traceId,
    // instance, and the RFC 9110 fallbacks — is exercised rather than stubbed away.
    private readonly GlobalExceptionHandler _handler =
        new(CreateProblemDetailsService(), NullLogger<GlobalExceptionHandler>.Instance);

    [Fact]
    public async Task TryHandleAsync_ForbiddenException_Returns403WithMessage()
    {
        var httpContext = CreateHttpContext();
        var exception = new ForbiddenException("You do not have permission to use MCP server 'docs'.");

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(exception.Message, problem.Detail);
        Assert.Equal(ProblemTypes.Forbidden.Type, problem.Type);
    }

    [Fact]
    public async Task TryHandleAsync_McpAuthorizationRequiredException_Returns403WithMessage()
    {
        var httpContext = CreateHttpContext();
        var exception = new McpAuthorizationRequiredException("docs", new InvalidOperationException("consent required"));

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal("Consent or additional authentication is required for MCP server 'docs'.", problem.Detail);
        // The distinct type is the only thing separating this from an ordinary 403 denial.
        Assert.Equal(ProblemTypes.McpAuthorizationRequired.Type, problem.Type);
        Assert.Equal("docs", ReadExtension(problem, "serverName"));
    }

    [Fact]
    public async Task TryHandleAsync_McpServerUnavailableException_Returns502WithMessage()
    {
        var httpContext = CreateHttpContext();
        var exception = new McpServerUnavailableException("docs", new TimeoutException("connect timed out"));

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal("MCP server 'docs' is unavailable.", problem.Detail);
        Assert.Equal(ProblemTypes.McpServerUnavailable.Type, problem.Type);
        Assert.Equal("docs", ReadExtension(problem, "serverName"));
    }

    [Fact]
    public async Task TryHandleAsync_ProviderNotConfiguredException_Returns503WithTheProviderId()
    {
        // 503 rather than the 400 the InvalidOperationException arm above it produces: rebasing this
        // exception on InvalidOperationException would silently downgrade the contract, and only this
        // assertion would notice.
        var httpContext = CreateHttpContext();
        var exception = new ProviderNotConfiguredException(Providers.AmazonBedrock);

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(exception.Message, problem.Detail);
        Assert.Equal(ProblemTypes.ProviderNotConfigured.Type, problem.Type);
        Assert.Equal(Providers.AmazonBedrock.ToString(), ReadExtension(problem, "providerId"));
    }

    [Fact]
    public async Task TryHandleAsync_NotFoundException_Returns404WithMessage()
    {
        var httpContext = CreateHttpContext();
        var exception = new NotFoundException("MCP server not found.");

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(exception.Message, problem.Detail);
        Assert.Equal(ProblemTypes.NotFound.Type, problem.Type);
    }

    [Fact]
    public async Task TryHandleAsync_ConversationBusyException_Returns409WithTheConversationBusyType()
    {
        // Contention is retryable, so it must not collapse into the 400 bucket alongside
        // validation failures.
        var httpContext = CreateHttpContext();
        var exception = new ConversationBusyException("The conversation is already streaming a response.");

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(exception.Message, problem.Detail);
        Assert.Equal(ProblemTypes.ConversationBusy.Type, problem.Type);
    }

    [Fact]
    public async Task TryHandleAsync_InvalidOperationException_Returns400WithTheRfcType()
    {
        // A generic .NET exception is not a domain concept, so it carries no application-specific
        // type and ProblemDetailsDefaults fills in the RFC 9110 status-section link.
        var httpContext = CreateHttpContext();
        var exception = new InvalidOperationException("The model is not active.");

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(exception.Message, problem.Detail);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", problem.Type);
        Assert.Equal("Bad Request", problem.Title);
    }

    [Fact]
    public async Task TryHandleAsync_UnexpectedException_Returns500WithoutLeakingDetails()
    {
        var httpContext = CreateHttpContext();
        var exception = new Exception("internal details");

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal("An unexpected internal server error occurred.", problem.Detail);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.6.1", problem.Type);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xml")]
    public async Task TryHandleAsync_AcceptTheWriterDeclines_StillCarriesTypeTitleAndTraceId(string accept)
    {
        // The writer declines these and writes nothing, so the fallback branch runs — and it never
        // reaches CustomizeProblemDetails, which is where the RFC 9110 type and title come from.
        var httpContext = CreateHttpContext();
        httpContext.Request.Headers.Accept = accept;

        await _handler.TryHandleAsync(
            httpContext, new Exception("internal details"), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.6.1", problem.Type);
        Assert.Equal("An error occurred while processing your request.", problem.Title);
        Assert.Equal("An unexpected internal server error occurred.", problem.Detail);
        Assert.False(string.IsNullOrWhiteSpace(ReadExtension(problem, "traceId")));
        Assert.Equal("/api/test", problem.Instance);
    }

    [Fact]
    public async Task TryHandleAsync_BadHttpRequestException_UsesTheStatusKestrelChose()
    {
        // Kestrel raises this when a chunked upload trips the body-size limit
        // MaxUploadSizeEndpointFilter lowers; it must not read as a server fault.
        var httpContext = CreateHttpContext();
        var exception = new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal("Request body too large.", problem.Detail);
    }

    [Fact]
    public async Task TryHandleAsync_EveryProblem_CarriesATraceIdAndInstance()
    {
        var httpContext = CreateHttpContext();

        await _handler.TryHandleAsync(
            httpContext, new NotFoundException("Model not found."), TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(httpContext);
        Assert.False(string.IsNullOrWhiteSpace(ReadExtension(problem, "traceId")));
        Assert.Equal("/api/test", problem.Instance);
    }

    [Fact]
    public async Task TryHandleAsync_ResponseAlreadyStarted_WritesNothing()
    {
        // Belt and braces: ExceptionHandlerMiddleware already skips every handler once the response
        // has started, so this guard is not what keeps a faulting SSE stream intact.
        var httpContext = CreateHttpContext();
        MarkResponseStarted(httpContext);

        var handled = await _handler.TryHandleAsync(
            httpContext, new NotFoundException("Model not found."), TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(0, httpContext.Response.Body.Length);
    }
}
