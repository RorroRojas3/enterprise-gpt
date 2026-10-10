using Amazon.BedrockRuntime;
using Anthropic;
using Enterprise.Gpt.Api.Configuration.Providers;
using Enterprise.Gpt.Common.Constants;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Configuration.Providers;

/// <summary>
/// The three optional providers register their clients only when <c>&lt;Provider&gt;:Enabled</c> is set,
/// so a deployment that does not use one needs none of its settings.
/// </summary>
public class ProviderGatingTests
{
    public static TheoryData<string, string, Type?> OptionalProviders => new()
    {
        { "AzureAIFoundry", ChatClientKeys.AzureAIFoundry, null },
        { "AmazonBedrock", ChatClientKeys.AmazonBedrock, typeof(IAmazonBedrockRuntime) },
        { "Anthropic", ChatClientKeys.Anthropic, typeof(IAnthropicClient) }
    };

    [Theory]
    [MemberData(nameof(OptionalProviders))]
    public void AddProvider_Disabled_RegistersNoClient(string section, string key, Type? sdkClient)
    {
        var services = Register(section, enabled: false);

        Assert.False(HasKeyedChatClient(services, key));
        if (sdkClient is not null)
        {
            Assert.DoesNotContain(services, d => d.ServiceType == sdkClient);
        }
    }

    [Theory]
    [MemberData(nameof(OptionalProviders))]
    public void AddProvider_Enabled_RegistersTheKeyedChatClient(string section, string key, Type? sdkClient)
    {
        var services = Register(section, enabled: true);

        Assert.True(HasKeyedChatClient(services, key));
        if (sdkClient is not null)
        {
            Assert.Contains(services, d => d.ServiceType == sdkClient);
        }
    }

    #region Private methods

    private static ServiceCollection Register(string section, bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{section}:Enabled"] = enabled.ToString() })
            .Build();
        var services = new ServiceCollection();

        _ = section switch
        {
            "AzureAIFoundry" => services.AddAzureAIFoundryProvider(configuration),
            "AmazonBedrock" => services.AddAmazonBedrockProvider(configuration),
            "Anthropic" => services.AddAnthropicProvider(configuration),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
        };

        return services;
    }

    private static bool HasKeyedChatClient(ServiceCollection services, string key) =>
        services.Any(d => d.ServiceType == typeof(IChatClient) && Equals(d.ServiceKey, key));

    #endregion
}
