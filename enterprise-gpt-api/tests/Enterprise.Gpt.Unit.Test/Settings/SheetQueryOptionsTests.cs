using System.ComponentModel.DataAnnotations;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Settings;

/// <summary>
/// Covers the sheet-query section against the same options pipeline <c>Program.cs</c> builds, so a
/// range or a cross-field rule that contradicts the shipped defaults fails here rather than at host
/// start.
/// </summary>
public sealed class SheetQueryOptionsTests
{
    /// <remarks>
    /// The API's own <c>appsettings.json</c>, linked into this project's output rather than copied into
    /// a literal here, so a value edited in the real file cannot leave these tests green.
    /// </remarks>
    private static Dictionary<string, string?> ShippedDefaults =>
        new ConfigurationBuilder()
            .AddJsonFile("api-appsettings.json")
            .Build()
            .GetSection(SheetQueryOptions.SectionName)
            .AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    private static SheetQueryOptions Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        services.AddOptions<SheetQueryOptions>()
            .Bind(configuration.GetSection(SheetQueryOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.ToolTimeoutSeconds >= options.TimeoutSeconds,
                "SheetQuery:ToolTimeoutSeconds must be at least SheetQuery:TimeoutSeconds.");

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<SheetQueryOptions>>().Value;
    }

    /// <summary>
    /// The values <c>appsettings.json</c> actually ships. A validator that rejects them is a host that
    /// does not start.
    /// </summary>
    [Fact]
    public void Bind_TheShippedConfiguration_Validates()
    {
        var options = Resolve(ShippedDefaults);

        Assert.Equal(50, options.MaxResultRows);
        Assert.Equal(20_000, options.MaxResultCharacters);
        Assert.Equal(200, options.MaxGroups);
        Assert.Equal(5, options.MaxFilters);
        Assert.Equal(10, options.TimeoutSeconds);
        Assert.Equal(30, options.ToolTimeoutSeconds);
    }

    /// <summary>
    /// An environment that overrides nothing does not silently switch the tool on.
    /// </summary>
    /// <remarks>
    /// The class default is the only one asserted here. The committed <c>appsettings.json</c> sets the
    /// key deliberately, and an explicit setting is not a default — what it happens to say is an
    /// environment's own choice rather than a rule a test enforces, the same line
    /// <c>SummarizationOptionsTests</c> draws.
    /// </remarks>
    [Fact]
    public void Bind_NoConfigurationAtAll_LeavesTheToolDisabled()
    {
        Assert.False(Resolve([]).Enabled);
    }

    /// <summary>
    /// The class defaults must be as valid as the committed ones, or a fresh environment fails the same
    /// cross-field rule the shipped file passes.
    /// </summary>
    [Fact]
    public void Bind_NoConfigurationAtAll_ValidatesOnTheClassDefaults()
    {
        var options = Resolve([]);

        Assert.Equal(10, options.TimeoutSeconds);
        Assert.Equal(30, options.ToolTimeoutSeconds);
    }

    [Theory]
    [InlineData("SheetQuery:MaxResultRows", "0")]
    [InlineData("SheetQuery:MaxResultRows", "501")]
    [InlineData("SheetQuery:MaxResultCharacters", "999")]
    [InlineData("SheetQuery:MaxResultCharacters", "200001")]
    [InlineData("SheetQuery:MaxGroups", "0")]
    [InlineData("SheetQuery:MaxGroups", "2001")]
    [InlineData("SheetQuery:MaxFilters", "0")]
    [InlineData("SheetQuery:MaxFilters", "21")]
    [InlineData("SheetQuery:TimeoutSeconds", "0")]
    [InlineData("SheetQuery:TimeoutSeconds", "121")]
    [InlineData("SheetQuery:ToolTimeoutSeconds", "0")]
    [InlineData("SheetQuery:ToolTimeoutSeconds", "301")]
    public void Bind_ValueOutsideItsRange_IsRejected(string key, string value)
    {
        var settings = new Dictionary<string, string?>(ShippedDefaults) { [key] = value };

        Assert.Throws<OptionsValidationException>(() => Resolve(settings));
    }

    /// <summary>
    /// A call deadline shorter than its own statement's timeout would abandon every query before that
    /// timeout could ever report one.
    /// </summary>
    [Fact]
    public void Bind_ToolTimeoutBelowTheStatementTimeout_IsRejected()
    {
        var settings = new Dictionary<string, string?>(ShippedDefaults)
        {
            ["SheetQuery:TimeoutSeconds"] = "60",
            ["SheetQuery:ToolTimeoutSeconds"] = "59"
        };

        Assert.Throws<OptionsValidationException>(() => Resolve(settings));
    }

    [Fact]
    public void Bind_ToolTimeoutEqualToTheStatementTimeout_IsAccepted()
    {
        var settings = new Dictionary<string, string?>(ShippedDefaults)
        {
            ["SheetQuery:TimeoutSeconds"] = "60",
            ["SheetQuery:ToolTimeoutSeconds"] = "60"
        };

        Assert.Equal(60, Resolve(settings).ToolTimeoutSeconds);
    }

    /// <summary>
    /// Every numeric bound carries a range, so a nonsensical value is a startup failure rather than a
    /// cap the tool silently acts on.
    /// </summary>
    /// <remarks>
    /// Booleans are excluded, not overlooked: a switch has exactly two valid values and a range over
    /// them would assert nothing.
    /// </remarks>
    [Fact]
    public void Options_EverySettableBoundCarriesARange()
    {
        var unranged = typeof(SheetQueryOptions)
            .GetProperties()
            .Where(p => p.CanWrite && p.PropertyType != typeof(bool))
            .Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(decimal))
            .Where(p => p.GetCustomAttributes(typeof(RangeAttribute), inherit: false).Length == 0)
            .Select(p => p.Name);

        Assert.Empty(unranged);
    }
}
