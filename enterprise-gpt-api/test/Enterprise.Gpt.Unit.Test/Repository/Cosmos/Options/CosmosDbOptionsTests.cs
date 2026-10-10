using Enterprise.Gpt.Repository.Cosmos.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Repository.Cosmos.Options;

public class CosmosDbOptionsTests
{
    [Fact]
    public void Validate_NoLegacyContainerSetting_Succeeds()
    {
        Assert.Empty(Failures(ValidSettings()));
    }

    /// <summary>
    /// The cutover gate: a configuration still naming the pre-cutover container has not been migrated,
    /// and starting it would answer every transcript read with an empty result.
    /// </summary>
    [Theory]
    [InlineData("conversations")]
    [InlineData("")]
    public void Validate_LegacyContainerSettingPresent_IsRejected(string value)
    {
        var settings = ValidSettings();
        settings[CosmosDbOptions.LegacyContainerKey] = value;

        Assert.Contains(Failures(settings), message => message.StartsWith(CosmosDbOptions.LegacyContainerKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CosmosDb:ConnectionString", "CosmosDb:ConnectionString is required.")]
    [InlineData("CosmosDb:DatabaseId", "CosmosDb:DatabaseId is required.")]
    [InlineData("CosmosDb:TranscriptContainerId", "CosmosDb:TranscriptContainerId is required.")]
    public void Validate_RequiredSettingMissing_NamesTheKey(string key, string message)
    {
        var settings = ValidSettings();
        settings.Remove(key);

        Assert.Contains(message, Failures(settings));
    }

    #region Private methods

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["CosmosDb:ConnectionString"] = "AccountEndpoint=https://localhost:8081/;AccountKey=a2V5;",
        ["CosmosDb:DatabaseId"] = "enterprise-gpt",
        ["CosmosDb:TranscriptContainerId"] = "transcripts"
    };

    private static IReadOnlyList<string> Failures(Dictionary<string, string?> settings)
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build()
            .GetSection(CosmosDbOptions.SectionName)
            .Get<CosmosDbOptions>() ?? new CosmosDbOptions();

        return [.. new CosmosDbOptionsValidator().Validate(options).Errors.Select(e => e.ErrorMessage)];
    }

    #endregion
}
