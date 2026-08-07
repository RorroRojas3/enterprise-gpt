using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Api.ExceptionHandlers;
using Xunit;
using static Enterprise.Gpt.Unit.Test.TestInfrastructure.ProblemDetailsTestHelpers;

namespace Enterprise.Gpt.Unit.Test.ExceptionHandlers;

public class OperationCanceledExceptionHandlerTests
{
    private readonly OperationCanceledExceptionHandler _handler =
        new(NullLogger<OperationCanceledExceptionHandler>.Instance);

    [Fact]
    public async Task TryHandleAsync_NonCancellationException_ReturnsFalse()
    {
        var httpContext = CreateHttpContext();

        var handled = await _handler.TryHandleAsync(
            httpContext, new InvalidOperationException("unrelated"), TestContext.Current.CancellationToken);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_OperationCanceledException_Returns499WithNoBody()
    {
        // 499 carries no body deliberately: the client that would read it has already disconnected.
        var httpContext = CreateHttpContext();

        var handled = await _handler.TryHandleAsync(
            httpContext, new OperationCanceledException(), TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, httpContext.Response.StatusCode);
        Assert.Equal(0, httpContext.Response.Body.Length);
    }

    [Fact]
    public async Task TryHandleAsync_ResponseAlreadyStarted_LeavesTheStatusAlone()
    {
        // The status is already on the wire and assigning to it would throw. Defence in depth —
        // ExceptionHandlerMiddleware does not invoke handlers once the response has started.
        var httpContext = CreateHttpContext();
        MarkResponseStarted(httpContext);

        var handled = await _handler.TryHandleAsync(
            httpContext, new OperationCanceledException(), TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        Assert.Equal(0, httpContext.Response.Body.Length);
    }
}
