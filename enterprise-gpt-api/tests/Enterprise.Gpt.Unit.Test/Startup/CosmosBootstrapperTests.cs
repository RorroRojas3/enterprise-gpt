using Enterprise.Gpt.Api.Startup;
using Enterprise.Gpt.Entity.Transcripts;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Startup;

/// <summary>
/// The indexing policy is asserted here rather than against the Cosmos emulator: the Linux emulator
/// accepts a custom indexing policy and then ignores it, so reading one back there would prove
/// nothing about what production is provisioned with.
/// </summary>
public sealed class CosmosBootstrapperTests
{
    [Fact]
    public void CreateTranscriptContainerProperties_PartitionsOnTheSyntheticKey()
    {
        var properties = CosmosBootstrapper.CreateTranscriptContainerProperties("transcript");

        Assert.Equal("transcript", properties.Id);
        Assert.Equal(PartitionKeys.Path, properties.PartitionKeyPath);
    }

    // Every path here is written and read back beside its message and never filtered, ordered or
    // aggregated on, so indexing it is charged on every write and bought nothing.
    [Theory]
    [InlineData("/content/*")]
    [InlineData("/htmlContent/*")]
    [InlineData("/usage/*")]
    [InlineData("/feedback/*")]
    public void CreateTranscriptContainerProperties_ExcludesThePropertiesNothingQueriesOn(string path)
    {
        var properties = CosmosBootstrapper.CreateTranscriptContainerProperties("transcript");

        Assert.Contains(properties.IndexingPolicy.ExcludedPaths, x => x.Path == path);
    }

    [Fact]
    public void CreateTranscriptContainerProperties_IndexesEverythingElse()
    {
        var properties = CosmosBootstrapper.CreateTranscriptContainerProperties("transcript");

        // The read predicate filters on type and conversationId and orders by dateCreated, so all
        // three must be indexed; including /* covers them without naming each one and going stale
        // the moment a property is added.
        Assert.Contains(properties.IndexingPolicy.IncludedPaths, x => x.Path == "/*");
        Assert.DoesNotContain(properties.IndexingPolicy.ExcludedPaths, x => x.Path.StartsWith("/type", StringComparison.Ordinal));
        Assert.DoesNotContain(properties.IndexingPolicy.ExcludedPaths, x => x.Path.StartsWith("/conversationId", StringComparison.Ordinal));
        Assert.DoesNotContain(properties.IndexingPolicy.ExcludedPaths, x => x.Path.StartsWith("/dateCreated", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateTranscriptContainerProperties_IndexesConsistently()
    {
        var properties = CosmosBootstrapper.CreateTranscriptContainerProperties("transcript");

        Assert.Equal(Microsoft.Azure.Cosmos.IndexingMode.Consistent, properties.IndexingPolicy.IndexingMode);
        Assert.True(properties.IndexingPolicy.Automatic);
    }
}
