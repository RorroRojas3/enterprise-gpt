using System.Text.RegularExpressions;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace Enterprise.Gpt.Service.Documents.Chunking;

/// <summary>
/// Repacks extracted document segments into token-bounded, overlapping chunks suitable for embedding.
/// </summary>
public interface ITextChunker
{
    /// <summary>
    /// Maximum number of tokens any produced chunk may contain.
    /// </summary>
    int MaxTokens { get; }

    /// <summary>
    /// Number of tokens carried from the end of one chunk into the start of the next.
    /// </summary>
    int OverlapTokens { get; }

    /// <summary>Counts the tokens in <paramref name="text"/> using the embedding model's tokenizer.</summary>
    int CountTokens(string text);

    /// <summary>Splits in-order <paramref name="segments"/> into ordered, overlapping chunks.</summary>
    /// <returns>
    /// Chunks with dense, zero-based <see cref="TextChunkDto.Index"/> values; empty when there is no
    /// readable text.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is null.</exception>
    IReadOnlyList<TextChunkDto> Chunk(IReadOnlyList<DocumentSegmentDto> segments, CancellationToken cancellationToken);
}

/// <summary>
/// Token-accurate <see cref="ITextChunker"/> that packs document text into chunks of at most
/// <see cref="MaxTokens"/> tokens with <see cref="OverlapTokens"/> tokens of overlap between neighbours.
/// </summary>
/// <remarks>
/// Chunks deliberately span segments (split at paragraphs, then sentences, then token slices) so sparse
/// slides do not embed as tiny chunks. Singleton: building the tokenizer loads a large vocabulary.
/// </remarks>
public sealed partial class TokenTextChunker : ITextChunker
{
    private const string UnitSeparator = "\n";

    // Encoding of the text-embedding-3-* and ada-002 families, used when the configured name is a
    // deployment alias the tokenizer library does not recognize.
    private const string FallbackEncoding = "cl100k_base";

    private readonly ILogger<TokenTextChunker> _logger;
    private readonly Tokenizer _tokenizer;
    private readonly int _separatorTokens;

    public TokenTextChunker(
        IOptions<DocumentOptions> options,
        IOptions<AzureOpenAIOptions> azureOpenAIOptions,
        ILogger<TokenTextChunker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(azureOpenAIOptions);

        _logger = logger;

        var chunking = options.Value.Chunking;
        MaxTokens = chunking.MaxTokens;

        // Overlap must leave room for new content, or a chunk could consist entirely of the previous
        // chunk's tail and the packer would stop making forward progress.
        OverlapTokens = Math.Clamp(chunking.OverlapTokens, 0, Math.Max(0, MaxTokens / 2));

        if (OverlapTokens != chunking.OverlapTokens)
        {
            LogOverlapClamped(_logger, chunking.OverlapTokens, OverlapTokens, MaxTokens);
        }

        // Validated options, not a raw IConfiguration read that would silently fall back to
        // cl100k_base if the section were renamed.
        _tokenizer = CreateTokenizer(azureOpenAIOptions.Value.EmbeddingModel, logger);
        _separatorTokens = _tokenizer.CountTokens(UnitSeparator);
    }

    /// <inheritdoc />
    public int MaxTokens { get; }

    /// <inheritdoc />
    public int OverlapTokens { get; }

    /// <inheritdoc />
    public int CountTokens(string text)
    {
        return string.IsNullOrEmpty(text) ? 0 : _tokenizer.CountTokens(text);
    }

    /// <inheritdoc />
    public IReadOnlyList<TextChunkDto> Chunk(IReadOnlyList<DocumentSegmentDto> segments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);

        List<TextChunkDto> chunks = [];
        List<TextUnit> current = [];
        var currentTokens = 0;

        foreach (var unit in EnumerateUnits(segments, cancellationToken))
        {
            var cost = current.Count == 0 ? unit.TokenCount : unit.TokenCount + _separatorTokens;

            if (current.Count > 0 && currentTokens + cost > MaxTokens)
            {
                AddChunks(chunks, current);

                current = BuildOverlap(current);
                currentTokens = SumWithSeparators(current);

                // Units are capped at MaxTokens, so at worst this empties the list and the unit still
                // fits; the loop always makes forward progress.
                while (current.Count > 0 && currentTokens + unit.TokenCount + _separatorTokens > MaxTokens)
                {
                    current.RemoveAt(0);
                    currentTokens = SumWithSeparators(current);
                }
            }

            current.Add(unit);
            currentTokens = SumWithSeparators(current);
        }

        if (current.Count > 0)
        {
            AddChunks(chunks, current);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i].Index = i;
        }

        LogChunked(_logger, segments.Count, chunks.Count, MaxTokens, OverlapTokens);

        return chunks;
    }

    private sealed record TextUnit(string Text, int TokenCount, int? SourceNumber);

    #region Private methods

    private void AddChunks(List<TextChunkDto> chunks, List<TextUnit> units)
    {
        var text = string.Join(UnitSeparator, units.Select(u => u.Text)).Trim();
        if (text.Length == 0)
        {
            return;
        }

        var sourceNumber = units[0].SourceNumber;
        var tokenCount = _tokenizer.CountTokens(text);

        if (tokenCount <= MaxTokens)
        {
            chunks.Add(new TextChunkDto { SourceNumber = sourceNumber, TokenCount = tokenCount, Text = text });
            return;
        }

        // Not expected (separators are budgeted), but re-slicing keeps "no chunk exceeds MaxTokens"
        // true for every input instead of trusting the arithmetic.
        LogJoinedChunkSplit(_logger, tokenCount, MaxTokens);

        foreach (var slice in SplitByTokens(text, MaxTokens, CancellationToken.None))
        {
            chunks.Add(new TextChunkDto
            {
                SourceNumber = sourceNumber,
                TokenCount = _tokenizer.CountTokens(slice),
                Text = slice
            });
        }
    }

    private List<TextUnit> BuildOverlap(List<TextUnit> emitted)
    {
        if (OverlapTokens <= 0 || emitted.Count == 0)
        {
            return [];
        }

        List<TextUnit> carried = [];
        var total = 0;

        for (var i = emitted.Count - 1; i >= 0; i--)
        {
            var unit = emitted[i];
            var cost = carried.Count == 0 ? unit.TokenCount : unit.TokenCount + _separatorTokens;

            if (total + cost <= OverlapTokens)
            {
                carried.Insert(0, unit);
                total += cost;
                continue;
            }

            // Carry a token-level tail so long-paragraph documents still get the requested overlap
            // rather than all-or-nothing.
            var remaining = OverlapTokens - total - (carried.Count == 0 ? 0 : _separatorTokens);
            if (remaining > 0)
            {
                var tail = TakeTokenTail(unit.Text, remaining);
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    carried.Insert(0, new TextUnit(tail, _tokenizer.CountTokens(tail), unit.SourceNumber));
                }
            }

            break;
        }

        return carried;
    }

    private int SumWithSeparators(List<TextUnit> units)
    {
        if (units.Count == 0)
        {
            return 0;
        }

        return units.Sum(u => u.TokenCount) + ((units.Count - 1) * _separatorTokens);
    }

    private IEnumerable<TextUnit> EnumerateUnits(IReadOnlyList<DocumentSegmentDto> segments, CancellationToken cancellationToken)
    {
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            foreach (var paragraph in SplitParagraphs(segment.Text))
            {
                // Per paragraph too: plain-text and legacy-Word extractors return the whole file as one
                // segment, which would otherwise be uncancellable.
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var piece in SplitToFit(paragraph, SplitSentences, cancellationToken))
                {
                    var tokenCount = _tokenizer.CountTokens(piece);
                    if (tokenCount > 0)
                    {
                        yield return new TextUnit(piece, tokenCount, segment.SourceNumber);
                    }
                }
            }
        }
    }

    private IEnumerable<string> SplitToFit(string text, Func<string, IEnumerable<string>> finer, CancellationToken cancellationToken)
    {
        if (_tokenizer.CountTokens(text) <= MaxTokens)
        {
            yield return text;
            yield break;
        }

        var producedFinerPieces = false;

        foreach (var piece in finer(text))
        {
            if (piece == text)
            {
                // The finer split could not divide this text; fall through to token slicing.
                continue;
            }

            producedFinerPieces = true;

            foreach (var slice in SplitToFit(piece, _ => [], cancellationToken))
            {
                yield return slice;
            }
        }

        if (!producedFinerPieces)
        {
            foreach (var slice in SplitByTokens(text, MaxTokens, cancellationToken))
            {
                yield return slice;
            }
        }
    }

    private static IEnumerable<string> SplitParagraphs(string text)
    {
        return ParagraphBoundary()
            .Split(text.ReplaceLineEndings("\n"))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0);
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        return SentenceBoundary()
            .Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);
    }

    // Last resort for text with no boundaries at all (minified data, unspaced scripts).
    private IEnumerable<string> SplitByTokens(string text, int maxTokens, CancellationToken cancellationToken)
    {
        var start = 0;

        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = FitLength(text.AsSpan(start), maxTokens);
            var slice = text.Substring(start, length);
            start += length;

            if (!string.IsNullOrWhiteSpace(slice))
            {
                yield return slice;
            }
        }
    }

    // Cuts by character index, not token index: decoding a token-id range can split a character's
    // bytes, corrupting the text and re-encoding to a larger count. Always returns at least 1.
    private int FitLength(ReadOnlySpan<char> span, int maxTokens)
    {
        var length = _tokenizer.GetIndexByTokenCount(span, maxTokens, out var normalizedText, out _);

        // A normalizer would index the normalized text, not ours; Tiktoken has none, so this guards a
        // future change.
        if (normalizedText is not null)
        {
            length = 0;
        }

        length = SnapToCharacterBoundary(span, Math.Clamp(length, 0, span.Length));

        // An isolated prefix can encode differently than in context, so verify the budget.
        while (length > 0 && _tokenizer.CountTokens(span[..length]) > maxTokens)
        {
            length = SnapToCharacterBoundary(span, length - 1);
        }

        if (length > 0)
        {
            return length;
        }

        // A character that alone exceeds the budget is still emitted; returning zero would loop forever.
        return span.Length >= 2 && char.IsSurrogatePair(span[0], span[1]) ? 2 : 1;
    }

    private static int SnapToCharacterBoundary(ReadOnlySpan<char> span, int length)
    {
        return length > 0 && length < span.Length && char.IsHighSurrogate(span[length - 1])
            ? length - 1
            : length;
    }

    private string TakeTokenTail(string text, int tokenCount)
    {
        var index = _tokenizer.GetIndexByTokenCountFromEnd(text, tokenCount, out var normalizedText, out _);

        if (normalizedText is not null || index <= 0)
        {
            return _tokenizer.CountTokens(text) <= tokenCount ? text : string.Empty;
        }

        if (index >= text.Length)
        {
            return string.Empty;
        }

        // Never start on the low half of a surrogate pair.
        if (char.IsLowSurrogate(text[index]))
        {
            index++;
        }

        return text[index..];
    }

    private static TiktokenTokenizer CreateTokenizer(string? embeddingModel, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(embeddingModel))
        {
            try
            {
                var tokenizer = TiktokenTokenizer.CreateForModel(embeddingModel);
                LogChunkingTokenizer(logger, embeddingModel);
                return tokenizer;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                // Azure OpenAI uses deployment names, which often are not model ids the library knows.
                LogTokenizerFallback(logger, embeddingModel, FallbackEncoding);
            }
        }

        return TiktokenTokenizer.CreateForEncoding(FallbackEncoding);
    }

    [GeneratedRegex(@"\n[ \t]*\n[\s]*", RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphBoundary();

    // Whitespace after sentence-ending punctuation, or a single line break.
    [GeneratedRegex(@"(?<=[.!?…。！？])[ \t]+|\n", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundary();

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configured chunk overlap of {Configured} tokens was clamped to {Applied} for a maximum chunk size of {MaxTokens}")]
    private static partial void LogOverlapClamped(ILogger logger, int configured, int applied, int maxTokens);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chunked {SegmentCount} segment(s) into {ChunkCount} chunk(s) of at most {MaxTokens} tokens with {OverlapTokens} tokens of overlap")]
    private static partial void LogChunked(
        ILogger logger, int segmentCount, int chunkCount, int maxTokens, int overlapTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Joined chunk re-tokenized to {TokenCount} tokens, exceeding the {MaxTokens} token limit; splitting it")]
    private static partial void LogJoinedChunkSplit(ILogger logger, int tokenCount, int maxTokens);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chunking with the tokenizer for embedding model '{EmbeddingModel}'")]
    private static partial void LogChunkingTokenizer(ILogger logger, string embeddingModel);

    [LoggerMessage(Level = LogLevel.Information, Message = "'{EmbeddingModel}' is not a recognized tokenizer model name (it is likely a deployment name); falling back to the {Encoding} encoding")]
    private static partial void LogTokenizerFallback(ILogger logger, string embeddingModel, string encoding);

    #endregion
}
