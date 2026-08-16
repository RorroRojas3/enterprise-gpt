using Enterprise.Gpt.Service.Transcripts;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Transcripts;

public sealed class TranscriptBatchTests
{
    [Fact]
    public void Create_RecordsTheDocumentAndItsIdentifier()
    {
        var document = new { Value = 1 };
        var batch = new TranscriptBatch();

        batch.Create("message-1", document);

        var operation = Assert.Single(batch.Operations);
        Assert.Equal(TranscriptBatchOperationKinds.Create, operation.Kind);
        Assert.Equal("message-1", operation.Id);
        Assert.Same(document, operation.Item);
    }

    [Fact]
    public void Patch_RecordsTheOperationsInOrder()
    {
        var batch = new TranscriptBatch();

        batch.Patch("header", [PatchOperation.Increment("/messageCount", 2), PatchOperation.Set("/dateModified", "x")]);

        var operation = Assert.Single(batch.Operations);
        Assert.Equal(TranscriptBatchOperationKinds.Patch, operation.Kind);
        Assert.Equal(2, operation.PatchOperations!.Count);
        Assert.Equal("/messageCount", operation.PatchOperations[0].Path);
    }

    [Fact]
    public void Operations_AreOrderedAsRecorded()
    {
        var batch = new TranscriptBatch();

        batch.Create("user", new { })
            .Create("assistant", new { })
            .Patch("header", [PatchOperation.Increment("/messageCount", 2)]);

        Assert.Equal(
            [TranscriptBatchOperationKinds.Create, TranscriptBatchOperationKinds.Create, TranscriptBatchOperationKinds.Patch],
            batch.Operations.Select(x => x.Kind));
    }

    [Fact]
    public void Patch_MoreOperationsThanCosmosAccepts_Throws()
    {
        var batch = new TranscriptBatch();
        var operations = Enumerable
            .Range(0, TranscriptStore.MaxPatchOperations + 1)
            .Select(index => PatchOperation.Set($"/p{index}", index))
            .ToList();

        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Patch("header", operations));
    }

    [Fact]
    public void Create_NullDocument_Throws()
    {
        var batch = new TranscriptBatch();

        Assert.Throws<ArgumentNullException>(() => batch.Create<object>("id", null!));
    }

    [Fact]
    public void Describe_FailedBatch_NamesTheFailingOperationIndex()
    {
        var result = new TranscriptBatchResult(
            false, System.Net.HttpStatusCode.Conflict, 1, System.Net.HttpStatusCode.Conflict);

        Assert.Equal("batch failed at operation 1 with 409", result.Describe());
    }

    [Fact]
    public void Describe_SucceededBatch_ReportsSuccess()
    {
        var result = new TranscriptBatchResult(true, System.Net.HttpStatusCode.OK, null, null);

        Assert.Equal("batch succeeded (200)", result.Describe());
    }
}
