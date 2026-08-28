using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Settings;

/// <summary>
/// Covers the kill switch itself: a configuration reload has to reach the next turn, and a reload that
/// does not validate must not be the thing that fails one.
/// </summary>
public sealed class SheetQueryOptionsProviderTests : IDisposable
{
    private readonly FakeLogCollector _logs = new();
    private readonly List<ServiceProvider> _containers = [];

    /// <inheritdoc />
    public void Dispose()
    {
        // Disposing the container disposes the provider, releasing the change-token registration it holds.
        foreach (var container in _containers)
        {
            container.Dispose();
        }
    }

    [Fact]
    public void Current_NoConfiguration_ServesTheClassDefaults()
    {
        var (provider, _) = Build([]);

        Assert.False(provider.Current.Enabled);
        Assert.Equal(30, provider.Current.ToolTimeoutSeconds);
    }

    [Fact]
    public void Current_AfterTheSwitchIsTurnedOn_ServesTheNewValue()
    {
        var (provider, configuration) = Build(new() { ["SheetQuery:Enabled"] = "false" });

        configuration["SheetQuery:Enabled"] = "true";
        configuration.Reload();

        Assert.True(provider.Current.Enabled);
    }

    [Fact]
    public void Current_AfterTheSwitchIsTurnedBackOff_ServesTheNewValue()
    {
        var (provider, configuration) = Build(new() { ["SheetQuery:Enabled"] = "true" });

        configuration["SheetQuery:Enabled"] = "false";
        configuration.Reload();

        Assert.False(provider.Current.Enabled);
    }

    /// <summary>
    /// Startup validation cannot protect a value that arrives after startup, and a turn is the wrong
    /// place to discover a typo in a file nobody redeployed.
    /// </summary>
    [Fact]
    public void Current_ReloadedWithAValueOutsideItsRange_KeepsTheLastValidConfiguration()
    {
        var (provider, configuration) = Build(new()
        {
            ["SheetQuery:Enabled"] = "true",
            ["SheetQuery:MaxResultRows"] = "25"
        });

        configuration["SheetQuery:MaxResultRows"] = "0";
        configuration.Reload();

        Assert.True(provider.Current.Enabled);
        Assert.Equal(25, provider.Current.MaxResultRows);
    }

    [Fact]
    public void Current_ReloadedWithAValueThatBreaksACrossFieldRule_KeepsTheLastValidConfiguration()
    {
        var (provider, configuration) = Build(new()
        {
            ["SheetQuery:TimeoutSeconds"] = "10",
            ["SheetQuery:ToolTimeoutSeconds"] = "30"
        });

        configuration["SheetQuery:ToolTimeoutSeconds"] = "5";
        configuration.Reload();

        Assert.Equal(30, provider.Current.ToolTimeoutSeconds);
    }

    [Fact]
    public void Current_ReloadedWithAnInvalidConfiguration_LogsWhyItWasIgnored()
    {
        var (_, configuration) = Build(new() { ["SheetQuery:MaxGroups"] = "10" });

        configuration["SheetQuery:MaxGroups"] = "0";
        configuration.Reload();

        var record = Assert.Single(_logs.GetSnapshot(), r => r.Level == LogLevel.Error);
        Assert.Contains("SheetQuery", record.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason this does not read through <c>IOptionsMonitor</c>: that one revalidates on the
    /// configuration provider's own callback thread, so an invalid edit surfaces as an unhandled
    /// exception where nothing can catch it rather than as an ignored reload.
    /// </summary>
    [Fact]
    public void Reload_WithAnInvalidConfiguration_DoesNotThrowOnTheConfigurationCallback()
    {
        var (_, configuration) = Build(new() { ["SheetQuery:MaxGroups"] = "10" });

        configuration["SheetQuery:MaxGroups"] = "0";

        Assert.Null(Record.Exception(configuration.Reload));
    }

    /// <summary>
    /// A corrected file recovers on its own, so an operator does not have to restart to undo a typo.
    /// </summary>
    [Fact]
    public void Current_ReloadedBackToAValidConfiguration_ServesItAgain()
    {
        var (provider, configuration) = Build(new() { ["SheetQuery:MaxGroups"] = "10" });

        configuration["SheetQuery:MaxGroups"] = "0";
        configuration.Reload();

        configuration["SheetQuery:MaxGroups"] = "40";
        configuration.Reload();

        Assert.Equal(40, provider.Current.MaxGroups);
    }

    /// <summary>
    /// What the change-token registrations exist to be released: once disposed, the provider stops
    /// following the configuration it was built over.
    /// </summary>
    [Fact]
    public void Current_AfterDisposal_StopsFollowingReloads()
    {
        var (provider, configuration) = Build(new() { ["SheetQuery:MaxGroups"] = "10" });

        ((IDisposable)provider).Dispose();

        configuration["SheetQuery:MaxGroups"] = "40";
        configuration.Reload();

        Assert.Equal(10, provider.Current.MaxGroups);
    }

    /// <summary>
    /// The same options pipeline <c>Program.cs</c> builds, minus <c>ValidateOnStart</c>, which only moves
    /// the first validation earlier.
    /// </summary>
    private (ISheetQueryOptionsProvider Provider, IConfigurationRoot Configuration) Build(
        Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        services.AddSingleton<ILogger<SheetQueryOptionsProvider>>(new FakeLogger<SheetQueryOptionsProvider>(_logs));
        services.AddSingleton<ISheetQueryOptionsProvider, SheetQueryOptionsProvider>();
        services.AddOptions<SheetQueryOptions>()
            .Bind(configuration.GetSection(SheetQueryOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.ToolTimeoutSeconds >= options.TimeoutSeconds,
                "SheetQuery:ToolTimeoutSeconds must be at least SheetQuery:TimeoutSeconds.");

        var container = services.BuildServiceProvider();
        _containers.Add(container);

        return (container.GetRequiredService<ISheetQueryOptionsProvider>(), configuration);
    }
}
