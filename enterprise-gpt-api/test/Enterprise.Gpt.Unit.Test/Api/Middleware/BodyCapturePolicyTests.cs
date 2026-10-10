using Microsoft.AspNetCore.Http;
using Enterprise.Gpt.Api.Middleware;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Middleware;

public sealed class BodyCapturePolicyTests
{
    private static readonly string[] _allowedContentTypes =
        ["application/json", "application/problem+json", "text/plain"];

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("  application/json  ", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("application/vnd.enterprise+json", true)]
    [InlineData("text/plain", true)]
    [InlineData("text/event-stream", false)]
    [InlineData("multipart/form-data; boundary=----x", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("text/html", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedContentType_MediaType_MatchesOnlyTheAllowList(string? contentType, bool expected)
    {
        Assert.Equal(expected, BodyCapturePolicy.IsAllowedContentType(contentType, _allowedContentTypes));
    }

    [Theory]
    [InlineData("/api/documents", true)]
    [InlineData("/api/documents/upload", true)]
    [InlineData("/API/Documents/upload", true)]
    [InlineData("/api/document", false)]
    [InlineData("/api/models", false)]
    public void MatchesAnyPattern_PrefixPattern_MatchesThePathAndItsDescendants(string path, bool expected)
    {
        string[] patterns = ["/api/documents"];

        Assert.Equal(expected, BodyCapturePolicy.MatchesAnyPattern(new PathString(path), patterns));
    }

    [Theory]
    [InlineData("/api/conversations/abc-123/stream", true)]
    [InlineData("/api/conversations/abc-123/stream/extra", true)]
    [InlineData("/api/conversations/abc-123", false)]
    [InlineData("/api/conversations/stream", false)]
    [InlineData("/api/conversations/a/b/stream", false)]
    public void MatchesAnyPattern_WildcardSegment_MatchesExactlyOneSegment(string path, bool expected)
    {
        string[] patterns = ["/api/conversations/*/stream"];

        Assert.Equal(expected, BodyCapturePolicy.MatchesAnyPattern(new PathString(path), patterns));
    }

    [Fact]
    public void MatchesAnyPattern_NoPatterns_MatchesNothing()
    {
        Assert.False(BodyCapturePolicy.MatchesAnyPattern(new PathString("/api/documents"), []));
    }

    [Fact]
    public void MatchesAnyPattern_EmptyPath_MatchesNothing()
    {
        string[] patterns = ["/api/documents"];

        Assert.False(BodyCapturePolicy.MatchesAnyPattern(new PathString(null), patterns));
    }
}
