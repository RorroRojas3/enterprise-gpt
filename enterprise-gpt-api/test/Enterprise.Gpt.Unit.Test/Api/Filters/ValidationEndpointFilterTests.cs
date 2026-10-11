using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Api.Problems.Constants;
using Enterprise.Gpt.Dto.Actions.Projects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Enterprise.Gpt.Unit.Test.TestInfrastructure.ProblemDetailsTestHelpers;

namespace Enterprise.Gpt.Unit.Test.Api.Filters;

public sealed class ValidationEndpointFilterTests
{
    private readonly ValidationEndpointFilter<CreateProjectActionDto> _filter = new(new CreateProjectActionDtoValidator());

    [Fact]
    public async Task InvokeAsync_ValidRequest_InvokesTheEndpoint()
    {
        var context = EndpointFilterInvocationContext.Create(CreateContext(), new CreateProjectActionDto { Name = "aaa-project" });
        var invoked = false;

        await _filter.InvokeAsync(context, _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task InvokeAsync_InvalidRequest_ShortCircuitsWithAValidationProblem()
    {
        var httpContext = CreateContext();
        var context = EndpointFilterInvocationContext.Create(httpContext, new CreateProjectActionDto { Name = string.Empty });
        var invoked = false;

        var result = await _filter.InvokeAsync(context, _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.False(invoked);
        var problemResult = Assert.IsType<ProblemHttpResult>(result);
        await problemResult.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        var problem = await ReadProblemAsync(httpContext);
        Assert.Equal(ProblemTypes.Validation.Type, problem.Type);
        Assert.Equal(ProblemTypes.Validation.Title, problem.Title);
        Assert.Null(problem.Detail);
        Assert.True(problem.Errors.ContainsKey(nameof(CreateProjectActionDto.Name)));
        Assert.False(string.IsNullOrWhiteSpace(ReadExtension(problem, ProblemDetailsRegistration.TraceIdExtension)));
    }

    [Fact]
    public async Task InvokeAsync_NoArgumentOfTheValidatedType_Throws()
    {
        var context = EndpointFilterInvocationContext.Create(CreateContext(), Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(null)).AsTask());
    }

    #region Private methods

    private static DefaultHttpContext CreateContext()
    {
        var httpContext = CreateHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddEnterpriseGptProblemDetails()
            .BuildServiceProvider();
        return httpContext;
    }

    #endregion
}
