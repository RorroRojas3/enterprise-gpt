using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Filtering;
using FluentValidation;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Filtering;

/// <summary>
/// The accepted <c>?type=</c> tokens are written down twice — as the advertised <c>Supported</c> set
/// and as the private parse lookup — and nothing but this keeps them in step. Drift fails silently
/// and client-facing in both directions, exactly as <c>SortKeysTests</c> records for the sort keys.
/// </summary>
public sealed class DocumentTypeNamesTests
{
    [Fact]
    public void Supported_NamesEveryOriginExactlyOnce()
    {
        Assert.Equal(Enum.GetValues<ConversationDocumentTypes>().Length, DocumentTypeNames.Supported.Count);
        Assert.Equal(
            DocumentTypeNames.Supported.Count,
            DocumentTypeNames.Supported.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(DocumentTypeNames.Supported, token => Assert.False(string.IsNullOrWhiteSpace(token)));
    }

    [Fact]
    public void EverySupportedToken_ResolvesToADistinctOrigin()
    {
        var resolved = DocumentTypeNames.Supported.Select(DocumentTypeNames.Resolve).ToList();

        Assert.All(resolved, origin => Assert.NotNull(origin));
        Assert.Equal(DocumentTypeNames.Supported.Count, resolved.Distinct().Count());
    }

    [Fact]
    public void SupportedList_QuotesEveryTokenItAdvertises()
    {
        Assert.Equal(
            string.Join(", ", DocumentTypeNames.Supported.Select(token => $"'{token}'")),
            DocumentTypeNames.SupportedList);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NoValue_NarrowsNothing(string? type)
    {
        Assert.Null(DocumentTypeNames.Resolve(type));
    }

    [Theory]
    [InlineData("GENERATED")]
    [InlineData("Uploaded")]
    [InlineData("  generated  ")]
    public void Resolve_CasingAndPadding_AreIgnored(string type)
    {
        Assert.NotNull(DocumentTypeNames.Resolve(type));
    }

    [Fact]
    public void Resolve_UnsupportedToken_ThrowsNamingTheParameterAndTheSupportedSet()
    {
        var exception = Assert.Throws<ValidationException>(() => DocumentTypeNames.Resolve("assistant"));

        var failure = Assert.Single(exception.Errors);
        Assert.Equal(DocumentTypeNames.TypeKey, failure.PropertyName);
        Assert.Contains(DocumentTypeNames.SupportedList, failure.ErrorMessage);
    }
}
