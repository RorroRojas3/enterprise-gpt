using Enterprise.Gpt.Dto.Actions.Mcp;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Validators;

public class SaveMcpCredentialActionValidatorTests
{
    private static bool IsAccepted(string apiKey) =>
        new SaveMcpCredentialActionDtoValidator()
            .Validate(new SaveMcpCredentialActionDto { ApiKey = apiKey })
            .IsValid;

    [Theory]
    // The two shapes GitHub issues, which are what this rule has to admit unchanged.
    [InlineData("ghp_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijkl_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghij")]
    [InlineData("abcdefgh")]
    public void ApiKey_TokenAsIssued_IsAccepted(string apiKey)
    {
        Assert.True(IsAccepted(apiKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void ApiKey_TooShortToBeAToken_IsRejected(string apiKey)
    {
        Assert.False(IsAccepted(apiKey));
    }

    [Fact]
    public void ApiKey_LongerThanTheColumnCanHold_IsRejected()
    {
        Assert.True(IsAccepted(new string('a', McpCredentialRules.MaxLength)));
        Assert.False(IsAccepted(new string('a', McpCredentialRules.MaxLength + 1)));
    }

    [Theory]
    // A space would terminate the bearer token on the wire; the rest are a paste that caught
    // surrounding text.
    [InlineData("ghp_with a space")]
    [InlineData("ghp_with\ttab")]
    [InlineData("ghp_with\nnewline")]
    [InlineData("ghp_with\r\ncrlf")]
    [InlineData(" ghp_leadingspace")]
    [InlineData("ghp_trailingspace ")]
    [InlineData("ghp_nonéascii")]
    public void ApiKey_CarryingWhitespaceOrNonAscii_IsRejected(string apiKey)
    {
        Assert.False(IsAccepted(apiKey));
    }
}
