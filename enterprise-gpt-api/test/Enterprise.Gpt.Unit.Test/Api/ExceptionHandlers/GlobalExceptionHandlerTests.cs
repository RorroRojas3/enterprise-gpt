using Enterprise.Gpt.Api.ExceptionHandlers;
using Enterprise.Gpt.Api.Problems.Constants;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.ChatModels.Exceptions;
using Enterprise.Gpt.Service.Conversations.Chat.Exceptions;
using Enterprise.Gpt.Service.Conversations.Exceptions;
using Enterprise.Gpt.Service.Documents.Exceptions;
using Enterprise.Gpt.Service.Documents.Summarization.Exceptions;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Exports.Exceptions;
using Enterprise.Gpt.Service.McpServers.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Enterprise.Gpt.Unit.Test.TestInfrastructure.ProblemDetailsTestHelpers;

namespace Enterprise.Gpt.Unit.Test.Api.ExceptionHandlers;

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
    public async Task TryHandleAsync_McpCredentialRequiredException_Returns428NamingTheServer()
    {
        var httpContext = CreateHttpContext();
        var mcpServerId = Guid.NewGuid();
        var exception = new McpCredentialRequiredException(mcpServerId, "GitHub");

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(ProblemTypes.McpCredentialRequired.Type, problem.Type);
        Assert.Equal("GitHub", ReadExtension(problem, "serverName"));
        Assert.Equal(mcpServerId.ToString(), ReadExtension(problem, "mcpServerId"));
    }

    [Fact]
    public async Task TryHandleAsync_McpCredentialRejectedException_Returns428NamingTheServer()
    {
        var httpContext = CreateHttpContext();
        var mcpServerId = Guid.NewGuid();
        var exception = new McpCredentialRejectedException(
            mcpServerId, "GitHub", new HttpRequestException("unauthorized"));

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(ProblemTypes.McpCredentialRejected.Type, problem.Type);
        Assert.Equal("GitHub", ReadExtension(problem, "serverName"));
        Assert.Equal(mcpServerId.ToString(), ReadExtension(problem, "mcpServerId"));
    }

    [Fact]
    public async Task TryHandleAsync_ProviderNotConfiguredException_Returns503WithTheProviderId()
    {
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
    public async Task TryHandleAsync_StorageNotConfiguredException_Returns503()
    {
        var httpContext = CreateHttpContext();
        var exception = new StorageNotConfiguredException();

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        // Passed through rather than suppressed: the message names no account, container or credential.
        Assert.Equal(exception.Message, problem.Detail);
        Assert.Equal(ProblemTypes.StorageNotConfigured.Type, problem.Type);
    }

    /// <summary>
    /// Shares the 503 with the arms above but carries its own type, so a client can disable the one
    /// format rather than the whole download control.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_ExportRendererNotConfiguredException_Returns503WithTheFormat()
    {
        var httpContext = CreateHttpContext();
        var exception = new ExportRendererNotConfiguredException(ConversationExportFormats.Pdf);

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(exception.Message, problem.Detail);
        Assert.Equal(ProblemTypes.ExportRendererNotConfigured.Type, problem.Type);
        Assert.Equal("pdf", ReadExtension(problem, "format"));
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
    public async Task TryHandleAsync_SummarizerNotConfiguredException_Returns503WithTheProviderNotConfiguredType()
    {
        var httpContext = CreateHttpContext();
        var exception = new SummarizerNotConfiguredException(Guid.NewGuid());

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(exception.Message, problem.Detail);
        Assert.Equal(ProblemTypes.ProviderNotConfigured.Type, problem.Type);
    }

    [Fact]
    public async Task TryHandleAsync_ConflictExceptionWithoutAProblemCode_Returns409WithTheRfcType()
    {
        var httpContext = CreateHttpContext();
        var exception = new SummarizerProtectedException(Guid.NewGuid());

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(exception.Message, problem.Detail);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.10", problem.Type);
        Assert.Equal("Conflict", problem.Title);
    }

    [Theory]
    [MemberData(nameof(UnmappedFrameworkExceptions))]
    public async Task TryHandleAsync_UnmappedFrameworkException_Returns500WithoutLeakingDetails(Exception exception)
    {
        var httpContext = CreateHttpContext();

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal("An unexpected internal server error occurred.", problem.Detail);
    }

    public static TheoryData<Exception> UnmappedFrameworkExceptions =>
    [
        new InvalidOperationException("The model is not active."),
        new ArgumentException("bad argument"),
        new KeyNotFoundException("missing key")
    ];

    [Fact]
    public async Task TryHandleAsync_ValidationException_Returns400WithErrorsKeyedByProperty()
    {
        var httpContext = CreateHttpContext();
        var exception = new ValidationException(
        [
            new ValidationFailure("Name", "'Name' must not be empty."),
            new ValidationFailure("ContextWindowSize", "'Context Window Size' must be greater than 0.")
        ]);

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(2, problem.Errors.Count);
        Assert.Equal("'Name' must not be empty.", Assert.Single(problem.Errors["Name"]));
        Assert.Equal("'Context Window Size' must be greater than 0.", Assert.Single(problem.Errors["ContextWindowSize"]));
    }

    [Fact]
    public async Task TryHandleAsync_MultipleFailuresOnOneProperty_GroupsThemUnderOneKey()
    {
        var httpContext = CreateHttpContext();
        var exception = new ValidationException(
        [
            new ValidationFailure("Name", "'Name' must not be empty."),
            new ValidationFailure("Name", "'Name' must be 128 characters or fewer.")
        ]);

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(2, Assert.Single(problem.Errors).Value.Length);
    }

    [Fact]
    public async Task TryHandleAsync_ValidationException_UsesTheValidationProblemType()
    {
        var httpContext = CreateHttpContext();
        var exception = new ValidationException([new ValidationFailure("Name", "'Name' must not be empty.")]);

        await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(ProblemTypes.Validation.Type, problem.Type);
        Assert.Equal(ProblemTypes.Validation.Title, problem.Title);
        Assert.Null(problem.Detail);
    }

    [Fact]
    public async Task TryHandleAsync_CancelledByTheClient_Returns499WithNoBody()
    {
        // No body: the client that would read it has already disconnected.
        var httpContext = CreateHttpContext();
        httpContext.RequestAborted = new CancellationToken(canceled: true);

        var handled = await _handler.TryHandleAsync(
            httpContext, new OperationCanceledException(), TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, httpContext.Response.StatusCode);
        Assert.Equal(0, httpContext.Response.Body.Length);
    }

    [Fact]
    public async Task TryHandleAsync_CancelledWhileTheClientIsConnected_Returns500()
    {
        // An HttpClient timeout surfaces as a TaskCanceledException with the caller still waiting.
        var httpContext = CreateHttpContext();

        var handled = await _handler.TryHandleAsync(
            httpContext, new TaskCanceledException("The request timed out."), TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal("An unexpected internal server error occurred.", problem.Detail);
    }

    [Fact]
    public async Task TryHandleAsync_CancelledByTheClientAfterTheResponseStarted_LeavesTheStatusAlone()
    {
        var httpContext = CreateHttpContext();
        httpContext.RequestAborted = new CancellationToken(canceled: true);
        MarkResponseStarted(httpContext);

        var handled = await _handler.TryHandleAsync(
            httpContext, new OperationCanceledException(), TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        Assert.Equal(0, httpContext.Response.Body.Length);
    }

    [Fact]
    public async Task TryHandleAsync_UnexpectedException_Returns500WithoutLeakingDetails()
    {
        var httpContext = CreateHttpContext();
        var exception = new InvalidOperationException("internal details");

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
            httpContext, new InvalidOperationException("internal details"), TestContext.Current.CancellationToken);

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
