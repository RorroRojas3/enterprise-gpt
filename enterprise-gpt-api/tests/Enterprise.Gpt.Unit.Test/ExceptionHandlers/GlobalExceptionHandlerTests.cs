using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Api.ExceptionHandlers;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Exceptions;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.ExceptionHandlers;

public class GlobalExceptionHandlerTests
{
    private readonly GlobalExceptionHandler _handler = new(NullLogger<GlobalExceptionHandler>.Instance);

    private static DefaultHttpContext CreateHttpContext()
    {
        return new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }
        };
    }

    private static async Task<ErrorDto> ReadErrorAsync(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        var error = await JsonSerializer.DeserializeAsync<ErrorDto>(
            httpContext.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        return error;
    }

    [Fact]
    public async Task TryHandleAsync_ForbiddenException_Returns403WithMessage()
    {
        var httpContext = CreateHttpContext();
        var exception = new ForbiddenException("You do not have permission to use MCP server 'docs'.");

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        var error = await ReadErrorAsync(httpContext);
        Assert.Equal(exception.Message, Assert.Single(error.Errors));
    }

    [Fact]
    public async Task TryHandleAsync_McpAuthorizationRequiredException_Returns403WithMessage()
    {
        var httpContext = CreateHttpContext();
        var exception = new McpAuthorizationRequiredException("docs", new InvalidOperationException("consent required"));

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        var error = await ReadErrorAsync(httpContext);
        Assert.Equal("Consent or additional authentication is required for MCP server 'docs'.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task TryHandleAsync_McpServerUnavailableException_Returns502WithMessage()
    {
        var httpContext = CreateHttpContext();
        var exception = new McpServerUnavailableException("docs", new TimeoutException("connect timed out"));

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
        var error = await ReadErrorAsync(httpContext);
        Assert.Equal("MCP server 'docs' is unavailable.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task TryHandleAsync_NotFoundException_Returns404WithMessage()
    {
        var httpContext = CreateHttpContext();
        var exception = new NotFoundException("MCP server not found.");

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
        var error = await ReadErrorAsync(httpContext);
        Assert.Equal(exception.Message, Assert.Single(error.Errors));
    }

    [Fact]
    public async Task TryHandleAsync_UnexpectedException_Returns500WithoutLeakingDetails()
    {
        var httpContext = CreateHttpContext();
        var exception = new Exception("internal details");

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        var error = await ReadErrorAsync(httpContext);
        Assert.Equal("An unexpected internal server error occurred.", Assert.Single(error.Errors));
    }
}
