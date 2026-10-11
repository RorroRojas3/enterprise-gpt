using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Repository.Cosmos.Provisioning;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Repository.Cosmos.Provisioning;

/// <summary>
/// The indexing policy is asserted here, not on the Linux emulator, which accepts a custom one and then
/// ignores it.
/// </summary>
public sealed class CosmosResourceProvisionerTests
{
    [Fact]
    public void CreateTranscriptContainerProperties_PartitionsOnTheSyntheticKey()
    {
        var properties = CosmosResourceProvisioner.CreateTranscriptContainerProperties("transcript");

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
    [InlineData("/attachments/*")]
    public void CreateTranscriptContainerProperties_ExcludesThePropertiesNothingQueriesOn(string path)
    {
        var properties = CosmosResourceProvisioner.CreateTranscriptContainerProperties("transcript");

        Assert.Contains(properties.IndexingPolicy.ExcludedPaths, x => x.Path == path);
    }

    [Fact]
    public void CreateTranscriptContainerProperties_IndexesEverythingElse()
    {
        var properties = CosmosResourceProvisioner.CreateTranscriptContainerProperties("transcript");

        // Reads filter on type and conversationId and order by dateCreated; /* covers all three without
        // going stale when a property is added.
        Assert.Contains(properties.IndexingPolicy.IncludedPaths, x => x.Path == "/*");
        Assert.DoesNotContain(properties.IndexingPolicy.ExcludedPaths, x => x.Path.StartsWith("/type", StringComparison.Ordinal));
        Assert.DoesNotContain(properties.IndexingPolicy.ExcludedPaths, x => x.Path.StartsWith("/conversationId", StringComparison.Ordinal));
        Assert.DoesNotContain(properties.IndexingPolicy.ExcludedPaths, x => x.Path.StartsWith("/dateCreated", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateTranscriptContainerProperties_IndexesConsistently()
    {
        var properties = CosmosResourceProvisioner.CreateTranscriptContainerProperties("transcript");

        Assert.Equal(Microsoft.Azure.Cosmos.IndexingMode.Consistent, properties.IndexingPolicy.IndexingMode);
        Assert.True(properties.IndexingPolicy.Automatic);
    }
}
