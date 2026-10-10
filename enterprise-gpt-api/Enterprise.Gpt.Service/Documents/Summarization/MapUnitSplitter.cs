using System.Text;
using System.Text.RegularExpressions;
using Enterprise.Gpt.Service.Tokenization;

namespace Enterprise.Gpt.Service.Summarization;

/// <summary>
/// Splits an oversized document into units that each fit one summarizer call.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>TokenTextChunker</c>. That chunker is pinned to the retrieval geometry —
/// 512 tokens with 128 of overlap — because it produces units to embed and rank. Reusing it here
/// would split a document into hundreds of map units where a handful are needed, multiplying the
/// call count, and the cost, by roughly two orders of magnitude. Its splitting primitives are
/// private on a sealed class, and widening that contract to serve a second geometry would make one
/// type answer to two callers with opposite requirements.
/// </para>
/// <para>
/// No overlap between units. Overlap exists so a retrieval hit near a boundary still has its
/// context; a summary reads every unit exactly once, so overlap would only re-send — and re-bill —
/// text the neighbouring call already covered.
/// </para>
/// </remarks>
public static partial class MapUnitSplitter
{
    /// <summary>
    /// Splits text into units of at most <paramref name="maxTokens"/> tokens each.
    /// </summary>
    /// <param name="text">The document's reassembled text.</param>
    /// <param name="maxTokens">The most tokens one unit may carry.</param>
    /// <param name="estimator">The summarizer's own token estimator.</param>
    /// <param name="deploymentName">The summarizer's deployment name, for tokenizer selection.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>
    /// The units in document order. Every non-whitespace character of the input survives in exactly
    /// one unit — though a slice taken by length can land mid-word, which summarization tolerates.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="text"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="estimator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxTokens"/> is less than one.</exception>
    /// <remarks>
    /// Paragraphs are packed greedily up to the cap; a paragraph over the cap is split on sentence
    /// boundaries, and a sentence still over it is sliced by length. Token counts are measured per
    /// piece and summed rather than re-measured for every candidate join — the counts are very
    /// slightly sub-additive across a join, which the map-unit fraction's own headroom covers, and
    /// re-measuring would make splitting a large document quadratic.
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

        var units = new List<string>();
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

    #region Private methods

    /// <remarks>
    /// Yields pieces already known to fit the cap on their own, so the caller only has to decide
    /// where to start a new unit.
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
    /// <para>
    /// The last resort, for a "sentence" with no interior boundary to split on — a table dumped as
    /// one line, or a language this splitter's punctuation set does not describe. The cut length is
    /// found by halving rather than by a characters-per-token guess, so it holds for text whose
    /// density the guess would be wrong about, and it is snapped off a surrogate pair so a slice can
    /// never end mid-character.
    /// </para>
    /// <para>
    /// Each probe measures only the candidate slice, and starts from twice the length that last
    /// fitted rather than from the whole remaining tail. Measuring the tail every time would make
    /// this quadratic in the input — for the multi-megabyte single-line input this method exists to
    /// handle, tens of seconds of tokenizing and a large-object allocation per slice.
    /// </para>
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

            // The next slice starts from twice what just fitted, so a cut that had to halve several
            // times does not pay for those probes again on every subsequent slice.
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

        // Backing off to zero would yield an empty slice and never advance, so a pair that starts at
        // the very first position is taken whole. One character over budget beats a lone surrogate,
        // which is not a character at all.
        return snapped == 1 ? Math.Min(2, text.Length) : snapped - 1;
    }

    [GeneratedRegex(@"\n[ \t]*\n[\s]*")]
    private static partial Regex ParagraphBoundary();

    [GeneratedRegex(@"(?<=[.!?…。！？])[ \t]+|\n")]
    private static partial Regex SentenceBoundary();

    private readonly record struct TextPiece(string Text, long Tokens);

    #endregion
}
