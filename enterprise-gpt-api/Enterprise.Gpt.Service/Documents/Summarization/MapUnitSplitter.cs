using System.Text;
using System.Text.RegularExpressions;
using Enterprise.Gpt.Service.Conversations.Tokenization;

namespace Enterprise.Gpt.Service.Documents.Summarization;

/// <summary>
/// Splits an oversized document into units that each fit one summarizer call.
/// </summary>
/// <remarks>
/// Not <c>TokenTextChunker</c>, whose 512-token retrieval geometry would multiply map units, and cost, by
/// about a hundred. No overlap: a summary reads every unit once, so overlap would only re-bill text.
/// </remarks>
public static partial class MapUnitSplitter
{
    private readonly record struct TextPiece(string Text, long Tokens);

    #region Private methods

    /// <remarks>
    /// Every piece already fits the cap on its own, so the caller only decides where a unit starts.
    /// </remarks>
    private static IEnumerable<TextPiece> EnumeratePieces(
        string text,
        long maxTokens,
        ITokenEstimator estimator,
        string deploymentName,
        CancellationToken cancellationToken)
    {
        foreach (var paragraph in ParagraphBoundary().Split(text.ReplaceLineEndings("\n")))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(paragraph))
            {
                continue;
            }

            var tokens = estimator.CountTokens(paragraph, deploymentName);

            if (tokens <= maxTokens)
            {
                yield return new TextPiece(paragraph, tokens);
                continue;
            }

            foreach (var sentence in SplitOversized(
                paragraph, maxTokens, estimator, deploymentName, cancellationToken))
            {
                yield return sentence;
            }
        }
    }

    private static IEnumerable<TextPiece> SplitOversized(
        string paragraph,
        long maxTokens,
        ITokenEstimator estimator,
        string deploymentName,
        CancellationToken cancellationToken)
    {
        foreach (var sentence in SentenceBoundary().Split(paragraph))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(sentence))
            {
                continue;
            }

            var tokens = estimator.CountTokens(sentence, deploymentName);

            if (tokens <= maxTokens)
            {
                yield return new TextPiece(sentence, tokens);
                continue;
            }

            foreach (var slice in SliceByLength(
                sentence, maxTokens, estimator, deploymentName, cancellationToken))
            {
                yield return slice;
            }
        }
    }

    /// <remarks>
    /// Last resort for text with no interior boundary. Cuts by halving rather than guessing token density,
    /// and measures only the candidate slice: measuring the whole tail is quadratic on multi-megabyte lines.
    /// </remarks>
    private static IEnumerable<TextPiece> SliceByLength(
        string sentence,
        long maxTokens,
        ITokenEstimator estimator,
        string deploymentName,
        CancellationToken cancellationToken)
    {
        var remaining = sentence.AsMemory();
        var probe = remaining.Length;

        while (remaining.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var take = SnapToCharacterBoundary(remaining.Span, Math.Min(remaining.Length, probe));
            var tokens = estimator.CountTokens(remaining[..take].ToString(), deploymentName);

            while (tokens > maxTokens && take > 1)
            {
                take = SnapToCharacterBoundary(remaining.Span, take / 2);
                tokens = estimator.CountTokens(remaining[..take].ToString(), deploymentName);
            }

            var slice = remaining[..take];
            remaining = remaining[take..];

            // Start from twice what fitted, so earlier halvings are not repeated on every slice.
            probe = Math.Max(1, take * 2);

            if (!slice.Span.IsWhiteSpace())
            {
                yield return new TextPiece(slice.ToString(), tokens);
            }
        }
    }

    private static int SnapToCharacterBoundary(ReadOnlySpan<char> text, int length)
    {
        var snapped = Math.Clamp(length, 1, text.Length);

        if (snapped >= text.Length || !char.IsLowSurrogate(text[snapped]))
        {
            return snapped;
        }

        // Backing off to zero would never advance, so a leading pair is taken whole: one character over
        // budget beats a lone surrogate.
        return snapped == 1 ? Math.Min(2, text.Length) : snapped - 1;
    }

    [GeneratedRegex(@"\n[ \t]*\n[\s]*")]
    private static partial Regex ParagraphBoundary();

    [GeneratedRegex(@"(?<=[.!?…。！？])[ \t]+|\n")]
    private static partial Regex SentenceBoundary();

    #endregion

    #region Public static methods

    /// <summary>
    /// Splits text into units of at most <paramref name="maxTokens"/> tokens each.
    /// </summary>
    /// <returns>
    /// The units in document order. Every non-whitespace character lands in exactly one unit, though a
    /// length slice can split a word.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="estimator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxTokens"/> is less than one.</exception>
    /// <remarks>
    /// Piece counts are summed rather than re-measured per join: joins are slightly sub-additive, which the
    /// map-unit fraction's headroom covers, and re-measuring would be quadratic.
    /// </remarks>
    public static IReadOnlyList<string> Split(
        string text,
        long maxTokens,
        ITokenEstimator estimator,
        string deploymentName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 1);

        List<string> units = [];
        var current = new StringBuilder();
        long currentTokens = 0;

        foreach (var paragraph in EnumeratePieces(text, maxTokens, estimator, deploymentName, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (currentTokens > 0 && currentTokens + paragraph.Tokens > maxTokens)
            {
                units.Add(current.ToString());
                current.Clear();
                currentTokens = 0;
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(paragraph.Text);
            currentTokens += paragraph.Tokens;
        }

        if (current.Length > 0)
        {
            units.Add(current.ToString());
        }

        return units;
    }

    #endregion
}
