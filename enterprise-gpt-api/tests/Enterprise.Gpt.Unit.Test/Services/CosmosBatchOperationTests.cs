using Enterprise.Gpt.Service;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

/// <summary>
/// The batch surface exists so callers can assert on the shape of what they submitted, which is
/// the property these tests pin.
/// </summary>
public class CosmosBatchOperationTests
{
    /// <summary>
    /// The document's static type is carried into the operation rather than erased to
    /// <see cref="object"/>, which is what keeps the serializer writing the document's own shape.
    /// </summary>
    [Fact]
    public void CreateItem_TypedDocument_PreservesTheDocumentType()
    {
        var document = new TestDocument("m1");

        var operation = CosmosBatchOperation.CreateItem(document);

        var create = Assert.IsType<CreateItemOperation<TestDocument>>(operation);
        Assert.Same(document, create.Item);
    }

    [Fact]
    public void PatchItem_Operations_CarriesIdAndOperations()
    {
        var operations = new[] { PatchOperation.Increment("/messageCount", 2) };

        var operation = CosmosBatchOperation.PatchItem("c1", operations);

        var patch = Assert.IsType<PatchItemOperation>(operation);
        Assert.Equal("c1", patch.Id);
        Assert.Equal(operations, patch.Operations);
    }

    [Fact]
    public void DeleteItem_Id_CarriesId()
    {
        var operation = CosmosBatchOperation.DeleteItem("m1");

        var delete = Assert.IsType<DeleteItemOperation>(operation);
        Assert.Equal("m1", delete.Id);
    }

    private sealed record TestDocument(string Id);
}
