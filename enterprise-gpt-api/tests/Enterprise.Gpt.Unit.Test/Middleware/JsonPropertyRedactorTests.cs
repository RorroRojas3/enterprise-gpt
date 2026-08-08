using Microsoft.Extensions.Options;
using Enterprise.Gpt.Api.Middleware;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Middleware;

public sealed class JsonPropertyRedactorTests
{
    private readonly JsonPropertyRedactor _redactor = CreateRedactor();

    [Fact]
    public void Redact_TopLevelSensitiveProperty_ReplacesTheValue()
    {
        var result = _redactor.Redact("""{"name":"acme","password":"hunter2"}""", "application/json");

        Assert.Equal("""{"name":"acme","password":"[redacted]"}""", result);
    }

    [Fact]
    public void Redact_NestedSensitiveProperty_ReplacesTheValue()
    {
        var result = _redactor.Redact("""{"outer":{"inner":{"apiKey":"sk-1"}}}""", "application/json");

        Assert.Equal("""{"outer":{"inner":{"apiKey":"[redacted]"}}}""", result);
    }

    [Fact]
    public void Redact_SensitivePropertyInsideAnArray_ReplacesEveryOccurrence()
    {
        var result = _redactor.Redact("""{"items":[{"token":"a"},{"token":"b"}]}""", "application/json");

        Assert.Equal("""{"items":[{"token":"[redacted]"},{"token":"[redacted]"}]}""", result);
    }

    [Fact]
    public void Redact_PropertyNameDifferingOnlyByCase_IsStillRedacted()
    {
        var result = _redactor.Redact("""{"ApiKey":"sk-1","PASSWORD":"x"}""", "application/json");

        Assert.Equal("""{"ApiKey":"[redacted]","PASSWORD":"[redacted]"}""", result);
    }

    [Fact]
    public void Redact_SnakeCaseVariantOfAConfiguredName_IsStillRedacted()
    {
        // OAuth and OIDC payloads are snake_case while the rest of this API is camelCase; a list that had
        // to spell out both would quietly miss whichever spelling nobody thought of.
        var result = _redactor.Redact(
            """{"access_token":"eyJ","refresh-token":"r","id_token":"i"}""",
            "application/json");

        Assert.Equal(
            """{"access_token":"[redacted]","refresh-token":"[redacted]","id_token":"[redacted]"}""",
            result);
    }

    [Fact]
    public void Redact_PropertyMerelyContainingAConfiguredName_IsPreserved()
    {
        var result = _redactor.Redact("""{"tokenCount":42,"passwordPolicy":"strong"}""", "application/json");

        Assert.Equal("""{"tokenCount":42,"passwordPolicy":"strong"}""", result);
    }

    [Fact]
    public void Redact_NonStringSensitiveValue_IsStillReplaced()
    {
        var result = _redactor.Redact("""{"token":{"nested":"value"},"secret":[1,2]}""", "application/json");

        Assert.Equal("""{"token":"[redacted]","secret":"[redacted]"}""", result);
    }

    [Fact]
    public void Redact_JsonWithNoSensitiveProperties_PreservesTheContent()
    {
        var result = _redactor.Redact("""{"name":"acme","count":3,"active":true,"tags":["a"]}""", "application/json");

        Assert.Equal("""{"name":"acme","count":3,"active":true,"tags":["a"]}""", result);
    }

    [Fact]
    public void Redact_ProblemJsonContentType_IsTreatedAsJson()
    {
        var result = _redactor.Redact("""{"title":"Bad Request","secret":"x"}""", "application/problem+json");

        Assert.Equal("""{"title":"Bad Request","secret":"[redacted]"}""", result);
    }

    [Theory]
    [InlineData("""{"name":"acme","password":"hunt""")]
    [InlineData("""{"name":"acme","api_key":"sk-live""")]
    [InlineData("""{"name":"acme","connection_string":"Server=""")]
    [InlineData("""{"name":"acme","client-secret":"abc""")]
    public void Redact_TruncatedJsonNamingASensitiveProperty_DropsTheWholePayload(string body)
    {
        // A body cut at the byte cap is nearly always invalid JSON, so this fallback is a routine path
        // rather than a rare one — and it has to recognize the same snake_case spellings the structured
        // path does, or a truncated secret would be logged in full.
        var result = _redactor.Redact(body, "application/json");

        Assert.Equal(JsonPropertyRedactor.UnparseableMarker, result);
    }

    [Fact]
    public void Redact_TruncatedJsonWithNothingSensitive_KeepsWhatWasCaptured()
    {
        var result = _redactor.Redact("""{"name":"acm""", "application/json");

        Assert.Equal("""{"name":"acm""", result);
    }

    [Fact]
    public void Redact_PlainTextNamingASensitiveProperty_DropsTheWholePayload()
    {
        var result = _redactor.Redact("the apiKey is sk-live-1", "text/plain");

        Assert.Equal(JsonPropertyRedactor.UnparseableMarker, result);
    }

    [Fact]
    public void Redact_PlainTextWithNothingSensitive_IsUnchanged()
    {
        var result = _redactor.Redact("hello world", "text/plain");

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Redact_EmptyBody_IsUnchanged()
    {
        Assert.Equal(string.Empty, _redactor.Redact(string.Empty, "application/json"));
    }

    [Fact]
    public void Redact_NoConfiguredNames_LeavesTheBodyAlone()
    {
        var redactor = CreateRedactor(names: []);

        Assert.Equal(
            """{"password":"hunter2"}""",
            redactor.Redact("""{"password":"hunter2"}""", "application/json"));
    }

    [Fact]
    public void Redact_NullContentType_FallsBackToTheUnstructuredScan()
    {
        Assert.Equal(JsonPropertyRedactor.UnparseableMarker, _redactor.Redact("""{"password":"x"}""", contentType: null));
    }

    private static JsonPropertyRedactor CreateRedactor(IList<string>? names = null)
    {
        var options = RequestLoggingTestHarness.CreateDefaultedOptions();

        if (names is not null)
        {
            options.Bodies.RedactedPropertyNames = names;
        }

        return new JsonPropertyRedactor(Options.Create(options));
    }
}
