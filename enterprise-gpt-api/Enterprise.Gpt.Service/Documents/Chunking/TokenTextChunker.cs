using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Settings;
using System.Text.RegularExpressions;

namespace Enterprise.Gpt.Service.Chunking
{
    /// <summary>
    /// Token-accurate <see cref="ITextChunker"/> that packs document text into chunks of at most
    /// <see cref="MaxTokens"/> tokens with <see cref="OverlapTokens"/> tokens of overlap between neighbours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chunking is done in tokens rather than characters because tokens are what the embedding model
    /// actually bounds; a character budget over-shoots on prose and under-shoots on code, tables and
    /// non-Latin scripts.
    /// </para>
    /// <para>
    /// Text is first broken into <em>units</em> — paragraphs, then sentences, then raw token slices — and
    /// units are packed greedily. Splitting at natural boundaries first keeps most chunks from starting or
    /// ending mid-sentence, which measurably helps retrieval; the raw token slice is only ever reached by
    /// input that has no boundaries at all, such as minified data.
    /// </para>
    /// <para>
    /// Chunks deliberately span segment boundaries. Chunking per page or per slide would emit a chunk of a
    /// handful of tokens for every sparse slide, and those embed poorly.
    /// </para>
    /// <para>
    /// Registered as a singleton: building a tokenizer loads and indexes a large vocabulary, which must not
    /// happen per request.
    /// </para>
    /// </remarks>
    public sealed partial class TokenTextChunker : ITextChunker
    {
        private const string UnitSeparator = "\n";

        // Encoding used by the text-embedding-3-* and ada-002 model families. Only consulted when the
        // configured model name is a deployment alias the tokenizer library does not recognize.
        private const string FallbackEncoding = "cl100k_base";

        private readonly ILogger<TokenTextChunker> _logger;
        private readonly Tokenizer _tokenizer;
        private readonly int _separatorTokens;

        /// <summary>
        /// Initializes a new instance of the <see cref="TokenTextChunker"/> class.
        /// </summary>
        /// <param name="options">Chunking options; <c>OverlapTokens</c> is clamped below <c>MaxTokens</c>.</param>
        /// <param name="azureOpenAIOptions">
        /// Supplies the embedding deployment name, which selects the tokenizer. Taken from the
        /// validated options rather than read off <c>IConfiguration</c> by key, which couples this
        /// class to a section name nothing checks: the rename of that section to <c>AzureOpenAI</c>
        /// would have left a raw read returning nothing and silently falling back to
        /// <c>cl100k_base</c> — chunking every document against a tokenizer that need not match the
        /// one the embedding model actually uses.
        /// </param>
        /// <param name="logger">Logger used for tokenizer selection and chunking telemetry.</param>
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
                _logger.LogWarning("Configured chunk overlap of {Configured} tokens was clamped to {Applied} for a maximum chunk size of {MaxTokens}",
                    chunking.OverlapTokens, OverlapTokens, MaxTokens);
            }

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

            var chunks = new List<TextChunkDto>();
            var current = new List<TextUnit>();
            var currentTokens = 0;

            foreach (var unit in EnumerateUnits(segments, cancellationToken))
            {
                var cost = current.Count == 0 ? unit.TokenCount : unit.TokenCount + _separatorTokens;

                if (current.Count > 0 && currentTokens + cost > MaxTokens)
                {
                    AddChunks(chunks, current);

                    current = BuildOverlap(current);
                    currentTokens = SumWithSeparators(current);

                    // Drop carried-over units from the front until the incoming one fits. Units are capped
                    // at MaxTokens by the token-slice fallback, so worst case this empties the list and the
                    // unit still fits — the loop always terminates and always makes forward progress.
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

            _logger.LogInformation("Chunked {SegmentCount} segment(s) into {ChunkCount} chunk(s) of at most {MaxTokens} tokens with {OverlapTokens} tokens of overlap",
                segments.Count, chunks.Count, MaxTokens, OverlapTokens);

            return chunks;
        }

        #region Chunk assembly
        /// <summary>
        /// Materializes the accumulated units into one chunk, or into several if the joined text
        /// re-tokenizes to more than <see cref="MaxTokens"/>.
        /// </summary>
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

            // Belt and braces. Joining units is accounted for separator by separator, so this is not
            // expected to trigger; re-slicing rather than trusting the arithmetic keeps the "no chunk
            // exceeds MaxTokens" guarantee true for every possible input.
            _logger.LogWarning("Joined chunk re-tokenized to {TokenCount} tokens, exceeding the {MaxTokens} token limit; splitting it", tokenCount, MaxTokens);

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

        /// <summary>
        /// Selects the trailing units of a just-emitted chunk to seed the next one.
        /// </summary>
        private List<TextUnit> BuildOverlap(List<TextUnit> emitted)
        {
            if (OverlapTokens <= 0 || emitted.Count == 0)
            {
                return [];
            }

            var carried = new List<TextUnit>();
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

                // Only part of this unit fits. Carry its token-level tail so the overlap is the requested
                // size even for documents written as a few very long paragraphs, where whole-unit overlap
                // would otherwise be all-or-nothing.
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
        #endregion

        #region Unit enumeration
        /// <summary>
        /// Flattens segments into a stream of units small enough to pack, progressively falling back to
        /// coarser splits only where a finer one leaves something too large.
        /// </summary>
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
                    // Checked per paragraph, not just per segment: the plain-text and legacy-Word
                    // extractors return the whole file as a single segment, so a segment-level check alone
                    // would make a large upload effectively uncancellable through a long CPU-bound stretch.
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

        /// <summary>
        /// Returns <paramref name="text"/> unchanged when it already fits, otherwise the results of
        /// <paramref name="finer"/>, recursively, with a raw token slice as the final fallback.
        /// </summary>
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

        /// <summary>
        /// Slices <paramref name="text"/> into runs of at most <paramref name="maxTokens"/> tokens,
        /// preserving every character.
        /// </summary>
        /// <remarks>
        /// This is the last-resort path for text with no sentence or paragraph boundaries at all — scripts
        /// written without spaces, minified data — where the alternative is failing the whole document.
        /// </remarks>
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

        /// <summary>
        /// Returns the length of the longest prefix of <paramref name="span"/> that stays within the token
        /// budget, always at least one character so the caller cannot stall.
        /// </summary>
        /// <remarks>
        /// The cut is chosen by character index, not token index. Slicing a range of encoded token ids and
        /// decoding it back is not round-trip safe: a byte-pair encoding assigns tokens to byte sequences,
        /// so a cut inside a character's bytes yields replacement characters on both sides of the seam —
        /// which corrupts the text and re-encodes to a different, sometimes larger, token count than was
        /// taken. Cutting on a character boundary avoids both.
        /// </remarks>
        private int FitLength(ReadOnlySpan<char> span, int maxTokens)
        {
            var length = _tokenizer.GetIndexByTokenCount(span, maxTokens, out var normalizedText, out _);

            // A normalizer would make the returned index address the normalized text rather than ours.
            // None is configured for the Tiktoken tokenizers used here, so this guards a future change.
            if (normalizedText is not null)
            {
                length = 0;
            }

            length = SnapToCharacterBoundary(span, Math.Clamp(length, 0, span.Length));

            // Encoding an isolated prefix can differ from encoding it in context, so the budget is verified
            // rather than assumed.
            while (length > 0 && _tokenizer.CountTokens(span[..length]) > maxTokens)
            {
                length = SnapToCharacterBoundary(span, length - 1);
            }

            if (length > 0)
            {
                return length;
            }

            // A single character whose own encoding exceeds the budget still has to be emitted somewhere;
            // returning zero here would loop forever.
            return span.Length >= 2 && char.IsSurrogatePair(span[0], span[1]) ? 2 : 1;
        }

        /// <summary>
        /// Moves <paramref name="length"/> back off a trailing high surrogate so a slice never ends
        /// half-way through a surrogate pair.
        /// </summary>
        private static int SnapToCharacterBoundary(ReadOnlySpan<char> span, int length)
        {
            return length > 0 && length < span.Length && char.IsHighSurrogate(span[length - 1])
                ? length - 1
                : length;
        }

        /// <summary>
        /// Returns the tail of <paramref name="text"/> worth at most <paramref name="tokenCount"/> tokens.
        /// </summary>
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
        #endregion

        private static Tokenizer CreateTokenizer(string? embeddingModel, ILogger logger)
        {
            if (!string.IsNullOrWhiteSpace(embeddingModel))
            {
                try
                {
                    var tokenizer = TiktokenTokenizer.CreateForModel(embeddingModel);
                    logger.LogInformation("Chunking with the tokenizer for embedding model '{EmbeddingModel}'", embeddingModel);
                    return tokenizer;
                }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
                {
                    // Azure OpenAI addresses models by deployment name, which is chosen by whoever created
                    // the deployment and often is not a model identifier the tokenizer library knows.
                    logger.LogInformation(
                        "'{EmbeddingModel}' is not a recognized tokenizer model name (it is likely a deployment name); falling back to the {Encoding} encoding",
                        embeddingModel, FallbackEncoding);
                }
            }

            return TiktokenTokenizer.CreateForEncoding(FallbackEncoding);
        }

        /// <summary>A blank line, i.e. a paragraph break.</summary>
        [GeneratedRegex(@"\n[ \t]*\n[\s]*", RegexOptions.CultureInvariant)]
        private static partial Regex ParagraphBoundary();

        /// <summary>Whitespace following sentence-ending punctuation, or a single line break.</summary>
        [GeneratedRegex(@"(?<=[.!?…。！？])[ \t]+|\n", RegexOptions.CultureInvariant)]
        private static partial Regex SentenceBoundary();

        /// <summary>
        /// A span of text small enough to be packed into a chunk, tagged with where it came from.
        /// </summary>
        private sealed record TextUnit(string Text, int TokenCount, int? SourceNumber);
    }
}
