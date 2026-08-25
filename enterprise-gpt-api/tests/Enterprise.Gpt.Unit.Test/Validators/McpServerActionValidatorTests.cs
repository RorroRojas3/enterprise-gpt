using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Validators;

/// <summary>
/// The two MCP action validators are line-for-line copies of one another, so every rule is
/// asserted against both. A rule that drifts between create and update is otherwise invisible:
/// the endpoint tests exercise the POST path only.
/// </summary>
public class McpServerActionValidatorTests
{
    private static CreateMcpServerActionDto CreateRequest(string? iconKey) =>
        new()
        {
            Name = "docs-server",
            Description = "A documentation MCP server.",
            Url = "https://mcp.example.com/sse",
            AuthType = McpAuthTypes.None,
            IconKey = iconKey
        };

    private static UpdateMcpServerActionDto UpdateRequest(string? iconKey) =>
        new()
        {
            Name = "docs-server",
            Description = "A documentation MCP server.",
            Url = "https://mcp.example.com/sse",
            AuthType = McpAuthTypes.None,
            IconKey = iconKey
        };

    private static IReadOnlyList<string> CreateErrors(string? iconKey) =>
        [
            .. new CreateMcpServerActionDtoValidator()
                .Validate(CreateRequest(iconKey))
                .Errors.Where(e => e.PropertyName == nameof(CreateMcpServerActionDto.IconKey))
                .Select(e => e.ErrorMessage)
        ];

    private static IReadOnlyList<string> UpdateErrors(string? iconKey) =>
        [
            .. new UpdateMcpServerActionDtoValidator()
                .Validate(UpdateRequest(iconKey))
                .Errors.Where(e => e.PropertyName == nameof(UpdateMcpServerActionDto.IconKey))
                .Select(e => e.ErrorMessage)
        ];

    [Theory]
    [InlineData(null)]
    [InlineData("microsoft")]
    [InlineData("context7")]
    // A key this build ships no artwork for. Accepted on purpose: the API validates the shape of
    // this value and never its membership, so a newer client can register an icon without a
    // server deploy and an older one degrades to its generic glyph.
    [InlineData("future-brand")]
    public void IconKey_WellFormedOrAbsent_IsAccepted(string? iconKey)
    {
        Assert.Empty(CreateErrors(iconKey));
        Assert.Empty(UpdateErrors(iconKey));
    }

    [Theory]
    [InlineData("Microsoft Logo")]
    [InlineData("Microsoft")]
    [InlineData("micro_soft")]
    [InlineData("-microsoft")]
    [InlineData("microsoft-")]
    // Empty rather than null: stored as '' it would sit beside the NULLs meaning the same thing.
    [InlineData("")]
    public void IconKey_MalformedSlug_IsRejected(string iconKey)
    {
        var expected = "IconKey must be a lowercase slug such as 'microsoft'.";

        Assert.Contains(expected, CreateErrors(iconKey));
        Assert.Contains(expected, UpdateErrors(iconKey));
    }

    [Fact]
    public void IconKey_OverMaximumLength_IsRejected()
    {
        var tooLong = new string('a', 65);

        Assert.NotEmpty(CreateErrors(tooLong));
        Assert.NotEmpty(UpdateErrors(tooLong));
        Assert.Empty(CreateErrors(new string('a', 64)));
        Assert.Empty(UpdateErrors(new string('a', 64)));
    }

    private static IReadOnlyList<string> CreateHeaderErrors(IReadOnlyDictionary<string, string>? headers) =>
        [
            .. new CreateMcpServerActionDtoValidator()
                .Validate(new CreateMcpServerActionDto
                {
                    Name = "docs-server",
                    Description = "A documentation MCP server.",
                    Url = "https://mcp.example.com/sse",
                    AuthType = McpAuthTypes.None,
                    Headers = headers
                })
                .Errors.Where(e => e.PropertyName == nameof(CreateMcpServerActionDto.Headers))
                .Select(e => e.ErrorMessage)
        ];

    private static IReadOnlyList<string> UpdateHeaderErrors(IReadOnlyDictionary<string, string>? headers) =>
        [
            .. new UpdateMcpServerActionDtoValidator()
                .Validate(new UpdateMcpServerActionDto
                {
                    Name = "docs-server",
                    Description = "A documentation MCP server.",
                    Url = "https://mcp.example.com/sse",
                    AuthType = McpAuthTypes.None,
                    Headers = headers
                })
                .Errors.Where(e => e.PropertyName == nameof(UpdateMcpServerActionDto.Headers))
                .Select(e => e.ErrorMessage)
        ];

    private static void AssertRejected(IReadOnlyDictionary<string, string> headers)
    {
        Assert.NotEmpty(CreateHeaderErrors(headers));
        Assert.NotEmpty(UpdateHeaderErrors(headers));
    }

    private static void AssertAccepted(IReadOnlyDictionary<string, string>? headers)
    {
        Assert.Empty(CreateHeaderErrors(headers));
        Assert.Empty(UpdateHeaderErrors(headers));
    }

    [Fact]
    public void Headers_AbsentOrEmpty_IsAccepted()
    {
        AssertAccepted(null);
        AssertAccepted(new Dictionary<string, string>());
    }

    [Fact]
    public void Headers_TheRemoteAzureDevOpsSet_IsAccepted()
    {
        AssertAccepted(new Dictionary<string, string>
        {
            ["X-MCP-Readonly"] = "true",
            ["X-MCP-Toolsets"] = "repos,wit,wiki"
        });
    }

    [Theory]
    // The connection's own.
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("Host")]
    [InlineData("Accept")]
    // Transport control. These are *accepted* by `HttpRequestHeaders` and bind to the typed
    // properties the handler reads, so they change the connection silently rather than throwing.
    [InlineData("Connection")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Expect")]
    [InlineData("Upgrade")]
    [InlineData("TE")]
    [InlineData("Keep-Alive")]
    // The MCP protocol's. `CopyAdditionalHeaders` sets these before copying ours and appends
    // rather than replaces, so a stored one would ship alongside the real one — which no amount
    // of ordering on our side can prevent.
    [InlineData("Mcp-Session-Id")]
    [InlineData("mcp-session-id")]
    [InlineData("MCP-Protocol-Version")]
    [InlineData("Last-Event-ID")]
    // Content headers. `HttpRequestHeaders.TryAddWithoutValidation` refuses every one of them and
    // the SDK turns that into a throw while connecting.
    [InlineData("Content-Type")]
    [InlineData("Content-Length")]
    [InlineData("Content-Encoding")]
    [InlineData("Content-Disposition")]
    [InlineData("Allow")]
    [InlineData("Expires")]
    [InlineData("Last-Modified")]
    public void Headers_ReservedName_IsRejected(string name)
    {
        AssertRejected(new Dictionary<string, string> { [name] = "anything" });
    }

    [Theory]
    [InlineData("X MCP Readonly")]
    [InlineData("X_MCP_Readonly")]
    [InlineData("X-MCP-Readonly:")]
    [InlineData("Ünicode")]
    [InlineData("")]
    public void Headers_MalformedName_IsRejected(string name)
    {
        AssertRejected(new Dictionary<string, string> { [name] = "true" });
    }

    [Fact]
    public void Headers_NameOverMaximumLength_IsRejected()
    {
        AssertRejected(new Dictionary<string, string> { [new string('a', 65)] = "true" });
        AssertAccepted(new Dictionary<string, string> { [new string('a', 64)] = "true" });
    }

    [Fact]
    public void Headers_DuplicateNameIgnoringCase_IsRejected()
    {
        // A payload spelling one header two ways deserializes to two ordinal entries, and they
        // are one header on the wire — which of them survived would be an ordering accident.
        AssertRejected(new Dictionary<string, string>
        {
            ["X-MCP-Toolsets"] = "repos",
            ["x-mcp-toolsets"] = "wit"
        });
    }

    [Fact]
    public void Headers_ValueOutOfRange_IsRejected()
    {
        AssertRejected(new Dictionary<string, string> { ["X-MCP-Toolsets"] = "" });
        AssertRejected(new Dictionary<string, string> { ["X-MCP-Toolsets"] = new string('a', 257) });
        AssertAccepted(new Dictionary<string, string> { ["X-MCP-Toolsets"] = new string('a', 256) });
    }

    [Theory]
    // The first is the whole point of the rule: a CR/LF in a value splits one header into two.
    [InlineData("repos\r\nX-MCP-Readonly: false")]
    [InlineData("repos\n")]
    [InlineData("repos\t")]
    [InlineData("répos")]
    public void Headers_ValueOutsidePrintableAscii_IsRejected(string value)
    {
        AssertRejected(new Dictionary<string, string> { ["X-MCP-Toolsets"] = value });
    }

    [Fact]
    public void Headers_MoreThanMaximum_IsRejected()
    {
        AssertRejected(BuildSet(9, "v"));
        AssertAccepted(BuildSet(8, "v"));
    }

    [Fact]
    public void Headers_OverSerializedBudget_IsRejected()
    {
        // Eight headers each within the per-name and per-value caps still overflow the column,
        // which is why the budget is a rule of its own rather than an inference from the others.
        AssertRejected(BuildSet(8, new string('v', 256)));
    }

    private static Dictionary<string, string> BuildSet(int count, string value) =>
        Enumerable.Range(1, count).ToDictionary(i => $"X-MCP-Header-{i}", _ => value);
}
