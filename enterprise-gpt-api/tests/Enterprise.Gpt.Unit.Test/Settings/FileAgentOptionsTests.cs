using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Settings;

/// <summary>
/// Covers the File Agent section against the same options pipeline <c>Program.cs</c> builds, so a
/// range or a required-value rule that contradicts the shipped defaults fails here rather than at
/// host start.
/// </summary>
/// <remarks>
/// Constructing <see cref="FileAgentOptions"/> directly never runs a validator, so one that rejects
/// the committed configuration would pass every such test and still stop the API from booting.
/// </remarks>
public sealed class FileAgentOptionsTests
{
    /// <remarks>
    /// The API's own <c>appsettings.json</c>, linked into this project's output rather than copied
    /// into a literal here, so the two cannot drift.
    /// </remarks>
    private static Dictionary<string, string?> ShippedDefaults =>
        new ConfigurationBuilder()
            .AddJsonFile("api-appsettings.json")
            .Build()
            .GetSection(FileAgentOptions.SectionName)
            .AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    private static FileAgentOptions Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        services.AddOptions<FileAgentOptions>()
            .Bind(configuration.GetSection(FileAgentOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.ModelId != Guid.Empty, "FileAgent:ModelId is required.");

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<FileAgentOptions>>().Value;
    }

    /// <summary>
    /// Asserted against the committed file, not only against the class default, which is where this
    /// diverges from the summarizer's own settings test.
    /// </summary>
    /// <remarks>
    /// A run of this feature provisions a billed sandbox session, so it is off in every environment
    /// including development; switching it on is a per-environment override, never something the
    /// repository hands a developer by checkout.
    /// </remarks>
    [Fact]
    public void Bind_TheShippedConfiguration_LeavesFileGenerationOff()
    {
        Assert.False(Resolve(ShippedDefaults).Enabled);
    }

    [Fact]
    public void Bind_TheShippedConfiguration_Validates()
    {
        var options = Resolve(ShippedDefaults);

        Assert.NotEqual(Guid.Empty, options.ModelId);
        Assert.Equal(300, options.ToolTimeoutSeconds);
        Assert.Equal(3, options.MaxArtifactsPerRun);
        Assert.Equal(12, options.MaxIterationsPerRun);
    }

    /// <summary>
    /// An environment that names nothing but the model id gets the feature off and no ceiling — the
    /// resting position a fresh deployment inherits.
    /// </summary>
    [Fact]
    public void Bind_OnlyTheModelId_LeavesFileGenerationOffAndUnbounded()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["FileAgent:ModelId"] = "e8b961c6-1fb6-4050-a265-dc72e5eab622"
        });

        Assert.False(options.Enabled);
        Assert.Null(options.MaxRunsPerUserPerDay);
        Assert.Null(options.MaxSandboxSecondsPerUserPerDay);
    }

    /// <summary>
    /// The agent's own bounds must be valid on the class defaults alone, or an environment that
    /// overrides nothing has drifted from the one the committed file describes.
    /// </summary>
    [Fact]
    public void Bind_OnlyTheModelId_ValidatesOnTheClassDefaults()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["FileAgent:ModelId"] = "e8b961c6-1fb6-4050-a265-dc72e5eab622"
        });

        Assert.Equal(300, options.ToolTimeoutSeconds);
        Assert.Equal(3, options.MaxArtifactsPerRun);
        Assert.Equal(12, options.MaxIterationsPerRun);
        Assert.Equal(1, options.MaxVerificationRetries);
    }

    /// <summary>
    /// Validated regardless of the flag, matching the startup validator that runs whether or not the
    /// feature is switched on.
    /// </summary>
    [Fact]
    public void Bind_NoModelId_IsRejected()
    {
        Assert.Throws<OptionsValidationException>(() => Resolve([]));
    }

    [Theory]
    [InlineData("FileAgent:ToolTimeoutSeconds", "29")]
    [InlineData("FileAgent:ToolTimeoutSeconds", "1801")]
    [InlineData("FileAgent:MaxArtifactsPerRun", "0")]
    [InlineData("FileAgent:MaxArtifactsPerRun", "11")]
    [InlineData("FileAgent:MaxIterationsPerRun", "1")]
    [InlineData("FileAgent:MaxIterationsPerRun", "41")]
    [InlineData("FileAgent:MaxVerificationRetries", "-1")]
    [InlineData("FileAgent:MaxVerificationRetries", "4")]
    [InlineData("FileAgent:MaxRunsPerUserPerDay", "0")]
    [InlineData("FileAgent:MaxRunsPerUserPerDay", "10001")]
    [InlineData("FileAgent:MaxSandboxSecondsPerUserPerDay", "0")]
    [InlineData("FileAgent:MaxSandboxSecondsPerUserPerDay", "1000001")]
    public void Bind_ValueOutsideItsRange_IsRejected(string key, string value)
    {
        var settings = new Dictionary<string, string?>(ShippedDefaults) { [key] = value };

        Assert.Throws<OptionsValidationException>(() => Resolve(settings));
    }
}
