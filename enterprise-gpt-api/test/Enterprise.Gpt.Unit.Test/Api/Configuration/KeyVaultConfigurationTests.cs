using Enterprise.Gpt.Api.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Configuration;

/// <remarks>
/// The enabled path reaches a vault by design, so only its refusals run end to end; the paths that
/// must add nothing are asserted against <c>AddEnterpriseGptKeyVault</c> itself.
/// </remarks>
public sealed class KeyVaultConfigurationTests
{
    private const string VaultUri = "https://contoso.vault.azure.net/";

    private const string ClientId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void Resolve_SectionAbsent_AddsNoVault()
    {
        Assert.Null(KeyVaultConfiguration.Resolve(Configure([]), Environment("Production")));
    }

    [Fact]
    public void Resolve_EnabledWithoutAVaultUri_Throws()
    {
        var configuration = Configure(new() { ["KeyVault:Enabled"] = "true" });

        var exception = Assert.Throws<InvalidOperationException>(
            () => KeyVaultConfiguration.Resolve(configuration, Environment("Production")));

        Assert.Contains("KeyVault:VaultUri", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// appsettings.json commits an empty VaultUri, so setting only KeyVault__Enabled lands here.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EnabledWithABlankVaultUri_Throws(string vaultUri)
    {
        var configuration = Configure(new()
        {
            ["KeyVault:Enabled"] = "true",
            ["KeyVault:VaultUri"] = vaultUri
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => KeyVaultConfiguration.Resolve(configuration, Environment("Production")));

        Assert.Contains("KeyVault:VaultUri", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("contoso.vault.azure.net")]
    [InlineData("/contoso")]
    [InlineData("http://contoso.vault.azure.net/")]
    public void Resolve_EnabledWithAVaultUriThatIsNotAbsoluteHttps_Throws(string vaultUri)
    {
        var configuration = Configure(new()
        {
            ["KeyVault:Enabled"] = "true",
            ["KeyVault:VaultUri"] = vaultUri
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => KeyVaultConfiguration.Resolve(configuration, Environment("Production")));

        Assert.Contains("KeyVault:VaultUri", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Key Vault throttles per vault, so a faster poll is rejected rather than clamped.</summary>
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:00:59")]
    public void Resolve_ReloadIntervalBelowOneMinute_Throws(string reloadInterval)
    {
        var configuration = EnabledConfiguration(("KeyVault:ReloadInterval", reloadInterval));

        var exception = Assert.Throws<InvalidOperationException>(
            () => KeyVaultConfiguration.Resolve(configuration, Environment("Production")));

        Assert.Contains("KeyVault:ReloadInterval", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Pins the boundary itself, so relaxing the comparison cannot stay green.</summary>
    [Fact]
    public void Resolve_ReloadIntervalOfExactlyOneMinute_IsAccepted()
    {
        var configuration = EnabledConfiguration(("KeyVault:ReloadInterval", "00:01:00"));

        var source = KeyVaultConfiguration.Resolve(configuration, Environment("Production"));

        Assert.NotNull(source);
        Assert.Equal(TimeSpan.FromMinutes(1), source.ReloadInterval);
    }

    [Fact]
    public void Resolve_EnabledAndWellFormed_ReturnsTheConfiguredVault()
    {
        var configuration = EnabledConfiguration(
            ("KeyVault:ManagedIdentityClientId", ClientId),
            ("KeyVault:ReloadInterval", "01:00:00"));

        var source = KeyVaultConfiguration.Resolve(configuration, Environment("Production"));

        Assert.NotNull(source);
        Assert.Equal(new Uri(VaultUri), source.VaultUri);
        Assert.Equal(ClientId, source.ManagedIdentityClientId);
        Assert.Equal(TimeSpan.FromHours(1), source.ReloadInterval);
    }

    [Fact]
    public void Resolve_EnabledWithNoIntervalOrIdentity_ReadsOnceUnderTheDefaultCredential()
    {
        var source = KeyVaultConfiguration.Resolve(EnabledConfiguration(), Environment("Production"));

        Assert.NotNull(source);
        Assert.Null(source.ReloadInterval);
        Assert.Null(source.ManagedIdentityClientId);
    }

    /// <summary>
    /// A template's empty app setting must not become a user-assigned identity with an empty client id.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EnabledWithABlankManagedIdentityClientId_ReadsAsNoIdentity(string clientId)
    {
        var source = KeyVaultConfiguration.Resolve(
            EnabledConfiguration(("KeyVault:ManagedIdentityClientId", clientId)),
            Environment("Production"));

        Assert.NotNull(source);
        Assert.Null(source.ManagedIdentityClientId);
    }

    /// <remarks>
    /// Reads the API's real <c>appsettings.json</c>, linked into the output; a committed <c>true</c> would
    /// point every environment at a vault.
    /// </remarks>
    [Fact]
    public void Resolve_TheShippedDefaults_AddNoVault()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile("api-appsettings.json").Build();

        Assert.Null(KeyVaultConfiguration.Resolve(configuration, Environment("Production")));
    }

    /// <summary>
    /// Asserted on the registration, since building the client before reading the section would leave
    /// every <c>Resolve</c> test green.
    /// </summary>
    [Theory]
    [InlineData("Production", "false")]
    [InlineData("Testing", "true")]
    public void AddEnterpriseKeyVault_VaultNotWanted_AppendsNoConfigurationSource(
        string environmentName,
        string enabled)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:Enabled"] = enabled,
            ["KeyVault:VaultUri"] = VaultUri
        });

        var sourcesBefore = ((IConfigurationBuilder)builder.Configuration).Sources.Count;

        Assert.Null(builder.AddEnterpriseGptKeyVault());
        Assert.Equal(sourcesBefore, ((IConfigurationBuilder)builder.Configuration).Sources.Count);
    }

    [Fact]
    public void LogStatus_NoVault_ReportsNothing()
    {
        var collector = new FakeLogCollector();

        KeyVaultConfiguration.LogStatus(null, new FakeLogger(collector));

        Assert.Empty(collector.GetSnapshot());
    }

    [Fact]
    public void LogStatus_Vault_ReportsTheHostAndNotTheClientId()
    {
        var collector = new FakeLogCollector();
        var source = KeyVaultConfiguration.Resolve(
            EnabledConfiguration(("KeyVault:ManagedIdentityClientId", ClientId)),
            Environment("Production"));

        KeyVaultConfiguration.LogStatus(source, new FakeLogger(collector));

        var record = Assert.Single(collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("contoso.vault.azure.net", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientId, record.Message, StringComparison.Ordinal);
    }

    private static IConfiguration EnabledConfiguration(params (string Key, string Value)[] extras)
    {
        var settings = new Dictionary<string, string?>
        {
            ["KeyVault:Enabled"] = "true",
            ["KeyVault:VaultUri"] = VaultUri
        };

        foreach (var (key, value) in extras)
        {
            settings[key] = value;
        }

        return Configure(settings);
    }

    private static IConfiguration Configure(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static IHostEnvironment Environment(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);

        return environment;
    }
}
