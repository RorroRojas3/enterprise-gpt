using System.ComponentModel.DataAnnotations;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Settings;

/// <summary>
/// Covers the summarization section against the same options pipeline <c>Program.cs</c> builds, so
/// a range or a cross-field rule that contradicts the shipped defaults fails here rather than at
/// host start.
/// </summary>
/// <remarks>
/// Constructing <see cref="SummarizationOptions"/> directly — which every other test in this suite
/// does — never runs a validator, so a validator that rejects the committed configuration passes
/// every test and still stops the API from booting. This class exists to close that gap.
/// </remarks>
public sealed class SummarizationOptionsTests
{
    /// <remarks>
    /// The API's own <c>appsettings.json</c>, linked into this project's output rather than copied
    /// into a literal here. A duplicated table would drift: raising a fraction past its range in the
    /// real file would leave every test in this class green while the host refused to boot, which is
    /// precisely what this class exists to prevent.
    /// </remarks>
    private static Dictionary<string, string?> ShippedDefaults =>
        new ConfigurationBuilder()
            .AddJsonFile("api-appsettings.json")
            .Build()
            .GetSection(SummarizationOptions.SectionName)
            .AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    private static SummarizationOptions Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        services.AddOptions<SummarizationOptions>()
            .Bind(configuration.GetSection(SummarizationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.ModelId != Guid.Empty, "Summarization:ModelId is required.");

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<SummarizationOptions>>().Value;
    }

    /// <summary>
    /// The values <c>appsettings.json</c> actually ships. A validator that rejects them is a host
    /// that does not start.
    /// </summary>
    [Fact]
    public void Bind_TheShippedConfiguration_Validates()
    {
        var options = Resolve(ShippedDefaults);

        Assert.Equal(0.85, options.SafetyFraction);
        Assert.Equal(0.9, options.MapUnitFraction);
        Assert.Equal(4, options.MaxConcurrency);
        Assert.Equal(180, options.CallTimeoutSeconds);
        Assert.Equal(5, options.MaxCollapsePasses);
        Assert.Equal(2, options.MaxCallRetries);
        Assert.Equal(300, options.ToolTimeoutSeconds);
        Assert.Equal(40, options.MaxMapUnits);
        Assert.Equal(25, options.MaxDigestDocuments);
    }

    /// <summary>
    /// The committed configuration ships the feature off, which is what makes enabling it a
    /// deliberate act in every environment rather than something a deployment inherits.
    /// </summary>
    [Fact]
    public void Bind_TheShippedConfiguration_HasSummarizationDisabled()
    {
        Assert.False(Resolve(ShippedDefaults).Enabled);
    }

    /// <summary>
    /// The class default matches the shipped one, so an environment that overrides nothing does not
    /// silently switch the feature on.
    /// </summary>
    [Fact]
    public void Bind_OnlyTheModelId_LeavesSummarizationDisabled()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Summarization:ModelId"] = "c36e22ed-262a-47a1-b2ba-06a38355ae0f"
        });

        Assert.False(options.Enabled);
    }

    /// <summary>
    /// An environment that overrides nothing but the model id must be as valid as the committed one,
    /// or the class defaults and the shipped defaults have drifted apart.
    /// </summary>
    [Fact]
    public void Bind_OnlyTheModelId_ValidatesOnTheClassDefaults()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Summarization:ModelId"] = "c36e22ed-262a-47a1-b2ba-06a38355ae0f"
        });

        Assert.Equal(0.85, options.SafetyFraction);
        Assert.Equal(0.9, options.MapUnitFraction);
    }

    [Fact]
    public void Bind_NoModelId_IsRejected()
    {
        Assert.Throws<OptionsValidationException>(() => Resolve([]));
    }

    [Theory]
    [InlineData("Summarization:SafetyFraction", "1.5")]
    [InlineData("Summarization:SafetyFraction", "0")]
    [InlineData("Summarization:MapUnitFraction", "1.5")]
    [InlineData("Summarization:MaxConcurrency", "0")]
    [InlineData("Summarization:CallTimeoutSeconds", "0")]
    [InlineData("Summarization:MaxCollapsePasses", "0")]
    [InlineData("Summarization:MaxCallRetries", "-1")]
    [InlineData("Summarization:ToolTimeoutSeconds", "0")]
    [InlineData("Summarization:MaxMapUnits", "0")]
    [InlineData("Summarization:MaxDigestDocuments", "0")]
    public void Bind_ValueOutsideItsRange_IsRejected(string key, string value)
    {
        var settings = new Dictionary<string, string?>(ShippedDefaults) { [key] = value };

        Assert.Throws<OptionsValidationException>(() => Resolve(settings));
    }

    /// <summary>
    /// The two fractions are applied at different levels — the map-unit fraction multiplies what the
    /// safety fraction already produced — so a map unit is smaller than a call's budget by
    /// construction, and neither needs to be bounded by the other.
    /// </summary>
    [Fact]
    public void Bind_MapUnitFractionAboveTheSafetyFraction_IsAccepted()
    {
        var settings = new Dictionary<string, string?>(ShippedDefaults)
        {
            ["Summarization:SafetyFraction"] = "0.5",
            ["Summarization:MapUnitFraction"] = "1.0"
        };

        var options = Resolve(settings);

        Assert.Equal(0.5, options.SafetyFraction);
        Assert.Equal(1.0, options.MapUnitFraction);
    }

    /// <summary>
    /// Every numeric tuning property carries a range, so a nonsensical value is a startup failure
    /// rather than a budget the fit decision silently acts on.
    /// </summary>
    /// <remarks>
    /// Booleans are excluded, not overlooked: a switch has exactly two valid values and a range
    /// over them would assert nothing. Adding a numeric knob without a range is still a failure.
    /// </remarks>
    [Fact]
    public void Options_EverySettableTuningProperty_CarriesARange()
    {
        var unranged = typeof(SummarizationOptions)
            .GetProperties()
            .Where(p => p.CanWrite && p.PropertyType != typeof(Guid) && p.PropertyType != typeof(bool))
            .Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(decimal))
            .Where(p => p.GetCustomAttributes(typeof(RangeAttribute), inherit: false).Length == 0)
            .Select(p => p.Name);

        Assert.Empty(unranged);
    }
}
