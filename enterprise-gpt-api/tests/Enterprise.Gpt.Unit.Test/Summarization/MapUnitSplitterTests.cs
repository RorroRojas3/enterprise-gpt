using Enterprise.Gpt.Service.Summarization;
using Enterprise.Gpt.Service.Tokenization;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Summarization;

public sealed class MapUnitSplitterTests
{
    /// <summary>
    /// One token per whitespace-separated word. Deterministic, so a unit's measured size is exactly
    /// what the test arranged rather than whatever a real encoding happens to produce.
    /// </summary>
    private sealed class WordTokenEstimator : ITokenEstimator
    {
        public long CountTokens(string? text, string? deploymentName) =>
            string.IsNullOrWhiteSpace(text)
                ? 0
                : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// One token per character, for the length-slicing path a word-counting estimator cannot reach.
    /// </summary>
    private sealed class CharacterTokenEstimator : ITokenEstimator
    {
        public long CountTokens(string? text, string? deploymentName) => text?.Length ?? 0;
    }

    private static readonly ITokenEstimator Estimator = new WordTokenEstimator();

    private static IReadOnlyList<string> Split(string text, long maxTokens) =>
        MapUnitSplitter.Split(text, maxTokens, Estimator, "deployment", TestContext.Current.CancellationToken);

    private static string Paragraph(int words, string marker) =>
        string.Join(" ", Enumerable.Range(0, words).Select(i => $"{marker}{i}"));

    #region Sizing

    [Fact]
    public void Split_TextThatFits_ReturnsOneUnit()
    {
        var units = Split(Paragraph(50, "w"), maxTokens: 100);

        Assert.Single(units);
    }

    /// <summary>
    /// The regression guard for the mistake this splitter exists to avoid. Reusing the 512-token
    /// retrieval chunker would turn a document a shade over budget into hundreds of map calls
    /// instead of two.
    /// </summary>
    [Fact]
    public void Split_TextJustOverTheBudget_ReturnsTwoUnitsNotHundreds()
    {
        var text = string.Join(
            "\n\n", Enumerable.Range(0, 11).Select(i => Paragraph(10, $"p{i}w")));

        var units = Split(text, maxTokens: 100);

        Assert.Equal(2, units.Count);
    }

    [Fact]
    public void Split_EveryUnit_FitsTheBudget()
    {
        var text = string.Join(
            "\n\n", Enumerable.Range(0, 40).Select(i => Paragraph(30, $"p{i}w")));

        var units = Split(text, maxTokens: 100);

        Assert.All(units, unit => Assert.True(
            Estimator.CountTokens(unit, "deployment") <= 100,
            $"A unit carried {Estimator.CountTokens(unit, "deployment")} tokens against a budget of 100."));
    }

    [Fact]
    public void Split_Paragraphs_ArePackedGreedilyRatherThanOnePerUnit()
    {
        var text = string.Join(
            "\n\n", Enumerable.Range(0, 10).Select(i => Paragraph(10, $"p{i}w")));

        var units = Split(text, maxTokens: 100);

        Assert.Single(units);
    }

    #endregion

    #region Content preservation

    [Fact]
    public void Split_AllUnits_PreserveEveryWord()
    {
        var text = string.Join(
            "\n\n", Enumerable.Range(0, 25).Select(i => Paragraph(17, $"p{i}w")));

        var units = Split(text, maxTokens: 60);

        var original = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var rejoined = string.Join(" ", units)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(original, rejoined);
    }

    /// <summary>
    /// Overlap belongs to retrieval, where a hit near a boundary needs its context. A summary reads
    /// every unit once, so overlap would only re-send — and re-bill — the neighbour's text.
    /// </summary>
    [Fact]
    public void Split_Units_DoNotOverlap()
    {
        var text = string.Join(
            "\n\n", Enumerable.Range(0, 20).Select(i => Paragraph(10, $"p{i}w")));

        var units = Split(text, maxTokens: 50);

        var words = units.SelectMany(u => u.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(words.Count(), words.Distinct().Count());
    }

    #endregion

    #region Oversized pieces

    [Fact]
    public void Split_AParagraphLargerThanTheBudget_SplitsOnSentences()
    {
        var text = string.Join(
            " ", Enumerable.Range(0, 10).Select(i => $"{Paragraph(10, $"s{i}w")}."));

        var units = Split(text, maxTokens: 30);

        Assert.True(units.Count >= 4);
        Assert.All(units, unit => Assert.True(Estimator.CountTokens(unit, "deployment") <= 30));
    }

    /// <summary>
    /// A table dumped as one line, or a language whose punctuation this splitter does not describe,
    /// has no interior boundary to split on and still must not exceed the budget.
    /// </summary>
    [Fact]
    public void Split_ASentenceLargerThanTheBudget_SlicesByLength()
    {
        var units = Split(Paragraph(200, "w"), maxTokens: 25);

        Assert.True(units.Count >= 8);
        Assert.All(units, unit => Assert.True(Estimator.CountTokens(unit, "deployment") <= 25));
    }

    /// <summary>
    /// The one input with no interior boundary at all. Measured per character rather than per word,
    /// because a word-counting estimator makes an unbroken run one token and never reaches the
    /// length-slicing path this test exists to cover.
    /// </summary>
    [Fact]
    public void Split_ASingleWordLargerThanTheBudget_SlicesItByLength()
    {
        var estimator = new CharacterTokenEstimator();

        var units = MapUnitSplitter.Split(
            new string('x', 5_000), 100, estimator, "deployment", TestContext.Current.CancellationToken);

        // Halving lands on a power-of-two fraction of the probe rather than on the exact maximum,
        // so the slice count is bounded rather than precise — what matters is that it sliced at all.
        Assert.InRange(units.Count, 50, 200);
        Assert.All(units, unit => Assert.True(estimator.CountTokens(unit, "deployment") <= 100));
        Assert.Equal(5_000, units.Sum(u => u.Replace("\n", string.Empty).Length));
    }

    /// <summary>
    /// A surrogate pair must never be cut in half, which a naive length slice would do.
    /// </summary>
    [Fact]
    public void Split_TextOfSurrogatePairs_NeverCutsACharacterInHalf()
    {
        var estimator = new CharacterTokenEstimator();

        var units = MapUnitSplitter.Split(
            string.Concat(Enumerable.Repeat("😀", 500)),
            51,
            estimator,
            "deployment",
            TestContext.Current.CancellationToken);

        Assert.All(units, unit =>
        {
            Assert.False(char.IsLowSurrogate(unit[0]));
            Assert.False(char.IsHighSurrogate(unit[^1]));
        });
    }

    #endregion

    #region Guards

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_BlankText_Throws(string text)
    {
        Assert.Throws<ArgumentException>(() => Split(text, maxTokens: 100));
    }

    [Fact]
    public void Split_NonPositiveBudget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Split("some text.", maxTokens: 0));
    }

    [Fact]
    public void Split_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => MapUnitSplitter.Split(Paragraph(500, "w"), 10, Estimator, "deployment", cts.Token));
    }

    #endregion
}
