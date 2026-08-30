using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Service.Security;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Security;

public sealed class UserSecretProtectorTests
{
    private static readonly Guid _userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _serverId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// The framework's own in-memory key ring: real cryptography, and a fresh master key per
    /// instance — which is what makes two of them stand in for a key ring that has moved on.
    /// </summary>
    private static UserSecretProtector CreateProtector()
    {
        return new UserSecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<UserSecretProtector>.Instance);
    }

    [Fact]
    public void TryUnprotect_PayloadFromTheSameUserAndServer_RecoversTheCredential()
    {
        var protector = CreateProtector();
        var ciphertext = protector.Protect(_userId, _serverId, "github_pat_secret");

        Assert.True(protector.TryUnprotect(_userId, _serverId, ciphertext, out var plaintext));
        Assert.Equal("github_pat_secret", plaintext);
    }

    [Fact]
    public void Protect_TheSameCredentialTwice_ProducesDifferentPayloads()
    {
        var protector = CreateProtector();

        Assert.NotEqual(
            protector.Protect(_userId, _serverId, "github_pat_secret"),
            protector.Protect(_userId, _serverId, "github_pat_secret"));
    }

    [Fact]
    public void Protect_Credential_DoesNotAppearInThePayload()
    {
        var ciphertext = CreateProtector().Protect(_userId, _serverId, "github_pat_secret");

        Assert.DoesNotContain("github_pat_secret", ciphertext, StringComparison.Ordinal);
    }

    [Fact]
    public void TryUnprotect_PayloadOfAnotherUser_Fails()
    {
        var protector = CreateProtector();
        var ciphertext = protector.Protect(_userId, _serverId, "github_pat_secret");

        Assert.False(protector.TryUnprotect(Guid.NewGuid(), _serverId, ciphertext, out var plaintext));
        Assert.Null(plaintext);
    }

    [Fact]
    public void TryUnprotect_PayloadForAnotherServer_Fails()
    {
        var protector = CreateProtector();
        var ciphertext = protector.Protect(_userId, _serverId, "github_pat_secret");

        Assert.False(protector.TryUnprotect(_userId, Guid.NewGuid(), ciphertext, out _));
    }

    [Fact]
    public void TryUnprotect_PayloadFromAnotherKeyRing_FailsRatherThanThrowing()
    {
        var ciphertext = CreateProtector().Protect(_userId, _serverId, "github_pat_secret");

        Assert.False(CreateProtector().TryUnprotect(_userId, _serverId, ciphertext, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-protected-payload")]
    public void TryUnprotect_MalformedPayload_FailsRatherThanThrowing(string ciphertext)
    {
        Assert.False(CreateProtector().TryUnprotect(_userId, _serverId, ciphertext, out _));
    }
}
