using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Api.Problems;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Filters;

public sealed class MaxUploadSizeEndpointFilterTests
{
    private const long MaxBytes = 64 * 1024;

    private sealed class StubMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize { get; set; }
    }

    private static EndpointFilterInvocationContext CreateContext(long? contentLength, IHttpMaxRequestBodySizeFeature? sizeFeature = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentLength = contentLength;

        if (sizeFeature is not null)
        {
            httpContext.Features.Set(sizeFeature);
        }

        return EndpointFilterInvocationContext.Create(httpContext);
    }

    [Fact]
    public async Task Filter_ContentLengthWithinTheLimit_InvokesTheEndpoint()
    {
        var filter = MaxUploadSizeEndpointFilter.Require(MaxBytes);
        var invoked = false;

        var result = await filter(CreateContext(MaxBytes - 1), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(TypedResults.Accepted("/somewhere"));
        });

        Assert.True(invoked);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Filter_ContentLengthExactlyAtTheLimit_InvokesTheEndpoint()
    {
        var filter = MaxUploadSizeEndpointFilter.Require(MaxBytes);
        var invoked = false;

        await filter(CreateContext(MaxBytes), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task Filter_ContentLengthOverTheLimit_ShortCircuitsWithoutReadingTheBody()
    {
        // The point of the Content-Length check is to reject before a single byte is buffered.
        var filter = MaxUploadSizeEndpointFilter.Require(MaxBytes);
        var invoked = false;

        var result = await filter(CreateContext(MaxBytes + 1), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.False(invoked);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(ProblemTypes.UploadTooLarge.Type, problem.ProblemDetails.Type);
        Assert.Contains("maximum upload size", problem.ProblemDetails.Detail);
        Assert.Contains("64 KB", problem.ProblemDetails.Detail);
        Assert.Equal(MaxBytes, problem.ProblemDetails.Extensions["maxBytes"]);
    }

    [Fact]
    public async Task Filter_NoContentLength_LowersTheServerBodyLimitSoAChunkedUploadIsStillBounded()
    {
        var feature = new StubMaxRequestBodySizeFeature { MaxRequestBodySize = 30_000_000 };
        var filter = MaxUploadSizeEndpointFilter.Require(MaxBytes);

        await filter(CreateContext(contentLength: null, feature), _ => ValueTask.FromResult<object?>(null));

        Assert.Equal(MaxBytes, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task Filter_BodyLimitAlreadyReadOnly_ProceedsWithoutThrowing()
    {
        // The feature turns read-only once the body has started being read; assigning would throw.
        var feature = new StubMaxRequestBodySizeFeature { IsReadOnly = true, MaxRequestBodySize = 30_000_000 };
        var filter = MaxUploadSizeEndpointFilter.Require(MaxBytes);
        var invoked = false;

        await filter(CreateContext(contentLength: null, feature), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(invoked);
        Assert.Equal(30_000_000, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task Filter_ServerWithoutTheBodySizeFeature_ProceedsWithoutThrowing()
    {
        var filter = MaxUploadSizeEndpointFilter.Require(MaxBytes);
        var invoked = false;

        await filter(CreateContext(contentLength: 10), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(invoked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Require_NonPositiveLimit_Throws(long maxBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MaxUploadSizeEndpointFilter.Require(maxBytes));
    }
}
