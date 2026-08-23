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
}
