using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Options;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Options;

/// <summary>
/// Runs the host's options pipeline: constructing <see cref="SummarizationOptions"/> never validates, so a
/// rule contradicting the shipped defaults would pass every other test yet stop the API from booting.
/// </summary>
public sealed class SummarizationOptionsTests
{
    /// <remarks>
    /// The API's own <c>appsettings.json</c>, linked into the output rather than copied into a literal, so
    /// editing the real file cannot leave these tests green.
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

        services.AddSingleton<IValidator<SummarizationOptions>, SummarizationOptionsValidator>();
        services.AddOptions<SummarizationOptions>()
            .Bind(configuration.GetSection(SummarizationOptions.SectionName))
            .ValidateWithFluentValidation();

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
    /// An environment that overrides nothing does not silently switch the feature on.
    /// </summary>
    /// <remarks>
    /// Only the class default is asserted: the committed <c>appsettings.json</c> sets the key explicitly,
    /// which is an environment's choice rather than a default.
    /// </remarks>
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
    /// The map-unit fraction multiplies what the safety fraction produced, so a map unit is smaller than a
    /// call's budget by construction and neither needs to bound the other.
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
    /// Booleans are excluded: a range over two valid values would assert nothing.
    /// </remarks>
    [Fact]
    public void Options_EverySettableTuningProperty_HasAValidationRule()
    {
        var descriptor = new SummarizationOptionsValidator().CreateDescriptor();

        var unranged = typeof(SummarizationOptions)
            .GetProperties()
            .Where(p => p.CanWrite && p.PropertyType != typeof(Guid) && p.PropertyType != typeof(bool))
            .Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(decimal))
            .Where(p => !descriptor.GetRulesForMember(p.Name).Any())
            .Select(p => p.Name);

        Assert.Empty(unranged);
    }
}
