using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Api.ExceptionHandlers;
using Enterprise.Gpt.Api.Problems;
using Xunit;
using static Enterprise.Gpt.Unit.Test.TestInfrastructure.ProblemDetailsTestHelpers;

namespace Enterprise.Gpt.Unit.Test.ExceptionHandlers;

public class ValidationExceptionHandlerTests
{
    private readonly ValidationExceptionHandler _handler =
        new(CreateProblemDetailsService(), NullLogger<ValidationExceptionHandler>.Instance);

    [Fact]
    public async Task TryHandleAsync_NonValidationException_ReturnsFalse()
    {
        var httpContext = CreateHttpContext();

        var handled = await _handler.TryHandleAsync(
            httpContext, new InvalidOperationException("not a validation failure"), TestContext.Current.CancellationToken);

        Assert.False(handled);
        Assert.Equal(0, httpContext.Response.Body.Length);
    }

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
        // A flat message list collapsed these into unrelated entries; the dictionary has to keep both
        // messages attached to the field that produced them.
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
    public async Task TryHandleAsync_ResponseAlreadyStarted_WritesNothing()
    {
        var httpContext = CreateHttpContext();
        MarkResponseStarted(httpContext);
        var exception = new ValidationException([new ValidationFailure("Name", "'Name' must not be empty.")]);

        var handled = await _handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(0, httpContext.Response.Body.Length);
    }
}
