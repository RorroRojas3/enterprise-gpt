using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Settings;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Chunking;

public sealed class TokenTextChunkerTests
{
    private static TokenTextChunker CreateChunker(int maxTokens = 512, int overlapTokens = 128, string? embeddingModel = "text-embedding-3-small")
    {
        var options = Options.Create(new DocumentOptions
        {
            Chunking = new ChunkingOptions { MaxTokens = maxTokens, OverlapTokens = overlapTokens }
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AzureAIFoundry:EmbeddingModel"] = embeddingModel })
            .Build();

        return new TokenTextChunker(options, configuration, NullLogger<TokenTextChunker>.Instance);
    }

    private static List<DocumentSegmentDto> Segments(params string[] texts)
    {
        return [.. texts.Select((text, i) => new DocumentSegmentDto { SourceNumber = i + 1, Text = text })];
    }

    /// <summary>
    /// Builds prose made of individually identifiable sentences, so overlap between neighbouring chunks can
    /// be asserted by looking for shared sentences rather than by comparing opaque substrings.
    /// </summary>
    private static string NumberedSentences(int count)
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            builder.Append($"Sentence {i} describes an entirely unremarkable and deliberately verbose subject. ");
        }

        return builder.ToString();
    }

    [Fact]
    public void Chunk_NoSegments_ReturnsEmpty()
    {
        var chunker = CreateChunker();

        var chunks = chunker.Chunk([], TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\t  \n")]
    public void Chunk_SegmentWithoutText_ReturnsEmpty(string text)
    {
        var chunker = CreateChunker();

        var chunks = chunker.Chunk(Segments(text), TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_TextShorterThanMaxTokens_ReturnsSingleChunk()
    {
        var chunker = CreateChunker();

        var chunks = chunker.Chunk(Segments("A short paragraph that comfortably fits inside one chunk."), TestContext.Current.CancellationToken);

        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.Index);
        Assert.Equal(1, chunk.SourceNumber);
        Assert.Contains("short paragraph", chunk.Text);
        Assert.True(chunk.TokenCount > 0);
    }

    [Theory]
    [InlineData(512, 128)]
    [InlineData(128, 32)]
    [InlineData(64, 16)]
    public void Chunk_LongText_NoChunkExceedsMaxTokens(int maxTokens, int overlapTokens)
    {
        var chunker = CreateChunker(maxTokens, overlapTokens);

        var chunks = chunker.Chunk(Segments(NumberedSentences(400)), TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.TokenCount <= maxTokens,
            $"Chunk {chunk.Index} has {chunk.TokenCount} tokens, exceeding the limit of {maxTokens}."));
    }

    [Fact]
    public void Chunk_LongText_ReportedTokenCountMatchesTheTokenizer()
    {
        var chunker = CreateChunker(128, 32);

        var chunks = chunker.Chunk(Segments(NumberedSentences(200)), TestContext.Current.CancellationToken);

        Assert.All(chunks, chunk => Assert.Equal(chunker.CountTokens(chunk.Text), chunk.TokenCount));
    }

    [Fact]
    public void Chunk_LongText_IndicesAreDenseAndOrdered()
    {
        var chunker = CreateChunker(128, 32);

        var chunks = chunker.Chunk(Segments(NumberedSentences(200)), TestContext.Current.CancellationToken);

        Assert.Equal([.. Enumerable.Range(0, chunks.Count)], chunks.Select(c => c.Index));
    }

    [Fact]
    public void Chunk_LongText_NoChunkIsEmpty()
    {
        var chunker = CreateChunker(64, 16);

        var chunks = chunker.Chunk(Segments(NumberedSentences(200)), TestContext.Current.CancellationToken);

        Assert.All(chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.Text)));
    }

    [Fact]
    public void Chunk_WithOverlapConfigured_ConsecutiveChunksShareContent()
    {
        var chunker = CreateChunker(128, 32);

        var chunks = chunker.Chunk(Segments(NumberedSentences(200)), TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 2);

        for (var i = 1; i < chunks.Count; i++)
        {
            // The tail of the previous chunk is carried into the head of this one, so the sentence this
            // chunk opens with must also appear in its predecessor.
            var openingSentence = chunks[i].Text.Split('.', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

            Assert.Contains(openingSentence, chunks[i - 1].Text);
        }
    }

    [Theory]
    [InlineData(512, 128)]
    [InlineData(256, 64)]
    [InlineData(128, 32)]
    public void Chunk_WithOverlapConfigured_CarriesRoughlyTheRequestedNumberOfTokens(int maxTokens, int overlapTokens)
    {
        // Asserting only that *something* is shared would still pass if a single token were carried, which
        // would defeat the point of overlapping at all.
        var chunker = CreateChunker(maxTokens, overlapTokens);

        var chunks = chunker.Chunk(Segments(NumberedSentences(600)), TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 2);

        for (var i = 1; i < chunks.Count - 1; i++)
        {
            var shared = LongestSharedPrefixOfTail(chunks[i - 1].Text, chunks[i].Text);
            var sharedTokens = chunker.CountTokens(shared);

            // Overlap is assembled from whole sentences, so it lands near the budget rather than on it;
            // one sentence of slack in either direction is the granularity of the unit being carried.
            var oneSentence = chunker.CountTokens("Sentence 1 describes an entirely unremarkable and deliberately verbose subject.");

            Assert.True(sharedTokens >= overlapTokens - oneSentence,
                $"Chunks {i - 1}/{i} share only {sharedTokens} tokens, well short of the requested {overlapTokens}.");
            Assert.True(sharedTokens <= overlapTokens + oneSentence,
                $"Chunks {i - 1}/{i} share {sharedTokens} tokens, well beyond the requested {overlapTokens}.");
        }
    }

    /// <summary>
    /// Returns the longest prefix of <paramref name="next"/> that is also a suffix of
    /// <paramref name="previous"/> — the text actually carried between the two chunks.
    /// </summary>
    private static string LongestSharedPrefixOfTail(string previous, string next)
    {
        var maxLength = Math.Min(previous.Length, next.Length);

        for (var length = maxLength; length > 0; length--)
        {
            if (previous.AsSpan(previous.Length - length).SequenceEqual(next.AsSpan(0, length)))
            {
                return next[..length];
            }
        }

        return string.Empty;
    }

    [Fact]
    public void Chunk_WithoutOverlapConfigured_ConsecutiveChunksDoNotShareContent()
    {
        var chunker = CreateChunker(128, 0);

        var chunks = chunker.Chunk(Segments(NumberedSentences(200)), TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 2);

        for (var i = 1; i < chunks.Count; i++)
        {
            var openingSentence = chunks[i].Text.Split('.', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

            Assert.DoesNotContain(openingSentence, chunks[i - 1].Text);
        }
    }

    [Fact]
    public void Chunk_SingleUnbrokenRunLongerThanMaxTokens_SplitsRatherThanLoopingForever()
    {
        var chunker = CreateChunker(64, 16);

        // No spaces, no punctuation, no line breaks: nothing the paragraph or sentence splitter can use, so
        // this exercises the raw token-slice fallback and its termination guarantee.
        var chunks = chunker.Chunk(Segments(new string('x', 20_000)), TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.TokenCount <= 64));
    }

    [Fact]
    public void Chunk_ParagraphLongerThanMaxTokensWithoutSentenceBreaks_SplitsWithinLimit()
    {
        var chunker = CreateChunker(64, 16);
        var text = string.Join(' ', Enumerable.Repeat("alpha bravo charlie delta echo foxtrot", 300));

        var chunks = chunker.Chunk(Segments(text), TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.TokenCount <= 64));
    }

    [Fact]
    public void Chunk_NonLatinText_StaysWithinTokenLimit()
    {
        var chunker = CreateChunker(64, 16);
        var text = string.Concat(Enumerable.Repeat("这是一个用于测试分块逻辑的中文句子。", 200));

        var chunks = chunker.Chunk(Segments(text), TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.TokenCount <= 64));
    }

    /// <summary>
    /// Scripts with no ASCII whitespace or sentence punctuation, so the paragraph and sentence splitters
    /// find no boundary and every chunk goes through the raw token-slice fallback.
    /// </summary>
    public static TheoryData<string, int> UnsplittableScripts()
    {
        string[] scripts =
        [
            "இதுஒருசோதனைவாக்கியம்",   // Tamil
            "क्षत्रज्ञश्रीद्ध",                  // Devanagari with conjuncts
            "🙂👨‍👩‍👧‍👦🇯🇵🧑🏽‍🚀",              // emoji, including ZWJ sequences and skin-tone modifiers
            "こんにちは世界",              // Japanese
            "안녕하세요세계",              // Korean
        ];

        // Sizes chosen because each one produced an over-budget or lossy chunk before the slicer cut on
        // character boundaries; 189/190 are the smallest that failed for Tamil and Devanagari respectively.
        int[] tokenBudgets = [16, 24, 33, 47, 97, 189, 190];

        var data = new TheoryData<string, int>();
        foreach (var script in scripts)
        {
            foreach (var maxTokens in tokenBudgets)
            {
                data.Add(script, maxTokens);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UnsplittableScripts))]
    public void Chunk_ScriptWithNoSplittableBoundary_StillNeverExceedsMaxTokens(string script, int maxTokens)
    {
        // Decoding an arbitrary range of token ids is not round-trip safe: a cut inside a multi-byte
        // grapheme re-encodes to more ids than were taken, so a naive slicer overshoots the ceiling.
        var chunker = CreateChunker(maxTokens, Math.Max(1, maxTokens / 4));
        var text = string.Concat(Enumerable.Repeat(script, 80));

        var chunks = chunker.Chunk(Segments(text), TestContext.Current.CancellationToken);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.True(
            chunker.CountTokens(chunk.Text) <= maxTokens,
            $"Chunk {chunk.Index} re-tokenizes to {chunker.CountTokens(chunk.Text)} tokens, exceeding the limit of {maxTokens}."));
    }

    [Theory]
    [MemberData(nameof(UnsplittableScripts))]
    public void Chunk_ScriptWithNoSplittableBoundary_PreservesEveryCharacter(string script, int maxTokens)
    {
        // Shrinking a slice to keep it within budget must advance by what was actually taken, or the
        // slicer silently drops text between chunks.
        var chunker = CreateChunker(maxTokens, overlapTokens: 0);
        var text = string.Concat(Enumerable.Repeat(script, 30));

        var chunks = chunker.Chunk(Segments(text), TestContext.Current.CancellationToken);

        Assert.Equal(text, string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public void Chunk_MultipleSegments_ChunkCarriesTheSourceNumberItStartsOn()
    {
        var chunker = CreateChunker(64, 0);

        var chunks = chunker.Chunk(
            Segments(NumberedSentences(30), NumberedSentences(30)),
            TestContext.Current.CancellationToken);

        Assert.Contains(chunks, chunk => chunk.SourceNumber == 1);
        Assert.Contains(chunks, chunk => chunk.SourceNumber == 2);
        Assert.All(chunks, chunk => Assert.True(chunk.SourceNumber is 1 or 2));
    }

    [Fact]
    public void Chunk_SegmentsWithoutSourceNumbers_LeavesSourceNumberNull()
    {
        var chunker = CreateChunker(64, 16);

        var chunks = chunker.Chunk(
            [new DocumentSegmentDto { SourceNumber = null, Text = NumberedSentences(60) }],
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Null(chunk.SourceNumber));
    }

    [Fact]
    public void Chunk_ShortSegments_ArePackedTogetherRatherThanEmittedIndividually()
    {
        var chunker = CreateChunker(512, 128);

        // Each "slide" holds a handful of tokens. Chunking per segment would emit ten tiny chunks, which
        // embed poorly; packing them is the point of chunking across segment boundaries.
        var segments = Segments([.. Enumerable.Range(1, 10).Select(i => $"Slide {i} title")]);

        var chunks = chunker.Chunk(segments, TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.Equal(1, chunks[0].SourceNumber);
        Assert.Contains("Slide 10 title", chunks[0].Text);
    }

    [Fact]
    public void Chunk_NullSegments_Throws()
    {
        var chunker = CreateChunker();

        Assert.Throws<ArgumentNullException>(() => chunker.Chunk(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_OverlapNotSmallerThanMaxTokens_ClampsSoChunkingStillMakesProgress()
    {
        var chunker = CreateChunker(maxTokens: 64, overlapTokens: 64);

        Assert.True(chunker.OverlapTokens < chunker.MaxTokens);

        var chunks = chunker.Chunk(Segments(NumberedSentences(200)), TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.TokenCount <= 64));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("my-embedding-deployment")]
    public void Constructor_UnrecognizedEmbeddingModelName_FallsBackToADefaultTokenizer(string? embeddingModel)
    {
        // Azure OpenAI addresses models by deployment name, which is rarely a name the tokenizer library
        // recognizes; falling back must not throw at startup.
        var chunker = CreateChunker(embeddingModel: embeddingModel);

        Assert.True(chunker.CountTokens("hello world") > 0);
    }
}
