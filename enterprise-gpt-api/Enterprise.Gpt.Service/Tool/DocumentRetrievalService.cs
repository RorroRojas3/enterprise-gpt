using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Frozen;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// Searches the documents attached to a conversation, and to its project, for the passages most
/// relevant to a query.
/// </summary>
public interface IDocumentRetrievalService
{
    /// <summary>
    /// Resolves what a conversation's retrieval tool is allowed to search.
    /// </summary>
    /// <param name="conversationId">The conversation being answered.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The scope, carrying an empty document list when there is nothing to search.</returns>
    /// <remarks>
    /// The project is read from the conversation row rather than accepted from the caller, so a project's
    /// documents can only ever be reached through a conversation that is actually in it. A deactivated
    /// project is dropped from the returned scope, so its documents become unreachable even though the
    /// conversation still points at it.
    /// </remarks>
    Task<DocumentRetrievalScope> GetScopeAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the passages in <paramref name="scope"/> that best answer <paramref name="query"/>.
    /// </summary>
    /// <param name="scope">The corpus to search, from <see cref="GetScopeAsync"/>.</param>
    /// <param name="query">What to search for.</param>
    /// <param name="documentName">Restricts the search to one document by name, or <see langword="null"/> to search all of them.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// The matching passages, most relevant first. An empty result is a normal outcome and carries the
    /// names of the documents that were searched.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    Task<DocumentSearchResult> SearchAsync(DocumentRetrievalScope scope, string query, string? documentName, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Retrieval runs two passes and fuses them. The vector pass finds chunks whose meaning is close to the
/// query; the keyword pass finds chunks that contain its exact tokens. Neither subsumes the other —
/// embeddings are weak on part numbers, error codes and surnames, and keywords are blind to paraphrase —
/// so the ranks are combined with Reciprocal Rank Fusion rather than either being trusted alone.
/// </para>
/// <para>
/// What comes back is passages, not chunks. Ingestion cuts documents into 512-token chunks that overlap
/// by 128, which means a single chunk regularly stops mid-explanation and consecutive chunks repeat a
/// quarter of their text. Every match is therefore widened by its neighbours and contiguous runs are
/// merged into one passage with the duplicated seam removed, so the model reads whole thoughts and pays
/// for each sentence once.
/// </para>
/// </remarks>
public sealed class DocumentRetrievalService(
    ILogger<DocumentRetrievalService> logger,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ITextChunker textChunker,
    IOptions<DocumentRetrievalOptions> options,
    EnterpriseGptDbContext ctx) : IDocumentRetrievalService
{
    /// <summary>
    /// Dimension of the stored embeddings, fixed by the <c>vector(1536)</c> column on
    /// <see cref="Entity.BaseDocumentChunk.Embedding"/>. A query vector of any other size is a
    /// misconfigured embedding deployment, and SQL Server would reject it with a type error that says
    /// nothing about the cause.
    /// </summary>
    private const int EmbeddingDimensions = 1536;

    /// <summary>
    /// The rank-fusion constant from the original Reciprocal Rank Fusion paper. It damps the influence
    /// of the very top ranks, so a chunk both passes agree on beats one that either ranks first alone.
    /// </summary>
    private const double RrfK = 60.0;

    /// <summary>
    /// Shortest seam the overlap stripper will act on. Below this, a match between the tail of one chunk
    /// and the head of the next is as likely to be a coincidence — a shared "the " — as real overlap.
    /// </summary>
    private const int MinOverlapChars = 16;

    /// <summary>
    /// Longest query accepted. Well beyond any real search phrase, and short enough that a runaway
    /// generation cannot turn one tool call into a large embedding request.
    /// </summary>
    private const int MaxQueryLength = 1024;

    private static readonly FrozenSet<string> StopWords = new[]
    {
        "a", "about", "after", "all", "also", "an", "and", "any", "are", "as", "at", "be", "because",
        "been", "but", "by", "can", "could", "did", "do", "does", "doing", "done", "for", "from", "get",
        "had", "has", "have", "how", "if", "in", "into", "is", "it", "its", "may", "me", "might", "more",
        "most", "must", "my", "no", "not", "of", "on", "one", "only", "or", "other", "our", "out", "over",
        "please", "said", "same", "shall", "should", "since", "so", "some", "such", "tell", "than", "that",
        "the", "their", "them", "then", "there", "these", "they", "this", "those", "to", "under", "up",
        "use", "used", "using", "was", "we", "were", "what", "when", "where", "which", "while", "who",
        "why", "will", "with", "would", "you", "your"
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ILogger<DocumentRetrievalService> _logger = logger;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = embeddingGenerator;
    private readonly ITextChunker _textChunker = textChunker;
    private readonly DocumentRetrievalOptions _options = options.Value;
    private readonly EnterpriseGptDbContext _ctx = ctx;

    /// <inheritdoc />
    public async Task<DocumentRetrievalScope> GetScopeAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        List<RetrievableDocument> documents = [];

        var conversationDocuments = await _ctx.ConversationDocuments
            .AsNoTracking()
            .Where(d => d.ConversationId == conversationId && !d.DateDeactivated.HasValue)
            .OrderBy(d => d.DateCreated)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        documents.AddRange(conversationDocuments.Select(d => new RetrievableDocument(d.Id, DocumentSource.Conversation, d.Name)));

        // Read from the conversation rather than taken from the caller, and confirmed active rather than
        // assumed. Reading it here ties the project to a conversation that is genuinely in it, so this
        // service cannot be asked for one project's documents through another project's conversation.
        // Nulling it for a deactivated project is what keeps the project half of every retrieval query
        // switched off for the rest of the turn, since the conversation still points at it.
        var activeProjectId = await _ctx.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId
                && c.ProjectId.HasValue
                && !c.Project!.DateDeactivated.HasValue)
            .Select(c => c.ProjectId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeProjectId.HasValue)
        {
            var projectDocuments = await _ctx.ProjectDocuments
                .AsNoTracking()
                .Where(d => d.ProjectId == activeProjectId.Value && !d.DateDeactivated.HasValue)
                .OrderBy(d => d.DateCreated)
                .Select(d => new { d.Id, d.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            documents.AddRange(projectDocuments.Select(d => new RetrievableDocument(d.Id, DocumentSource.Project, d.Name)));
        }

        return new DocumentRetrievalScope(conversationId, activeProjectId, documents);
    }

    /// <inheritdoc />
    public async Task<DocumentSearchResult> SearchAsync(DocumentRetrievalScope scope, string query, string? documentName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var trimmedQuery = query?.Trim() ?? string.Empty;

        if (trimmedQuery.Length == 0)
        {
            return EmptyResult(scope, trimmedQuery, "The query was empty. Call the tool again with the words to search for.");
        }

        // The query is model-supplied and goes straight to a billed embedding deployment, so its length
        // is bounded here rather than wherever it first causes trouble. The ceiling is far above any
        // real search phrase; anything longer is a runaway generation, not a question.
        if (trimmedQuery.Length > MaxQueryLength)
        {
            // Snapped off a trailing high surrogate so the echoed query never ends mid-character.
            var cut = char.IsHighSurrogate(trimmedQuery[MaxQueryLength - 1]) ? MaxQueryLength - 1 : MaxQueryLength;

            return EmptyResult(scope, trimmedQuery[..cut],
                $"The query was longer than {MaxQueryLength} characters and was not run. Search again with a short phrase or question.");
        }

        if (!scope.HasDocuments)
        {
            return EmptyResult(scope, trimmedQuery, "No documents are attached to this conversation.");
        }

        var (documentId, filterNote) = ResolveDocumentFilter(scope, documentName);

        var queryVector = await EmbedQueryAsync(trimmedQuery, cancellationToken).ConfigureAwait(false);
        var terms = ExtractTerms(trimmedQuery, _options.MaxQueryTerms);

        var dense = await RunDenseSearchAsync(scope, documentId, queryVector, cancellationToken).ConfigureAwait(false);
        var lexical = _options.EnableLexicalSearch && terms.Count > 0
            ? await RunLexicalSearchAsync(scope, documentId, terms, cancellationToken).ConfigureAwait(false)
            : [];

        var selected = SelectHits(dense, lexical, terms.Count);
        if (selected.Count == 0)
        {
            _logger.LogInformation(
                "Document search for conversation {ConversationId} matched none of {ChunkCount} candidate chunk(s).",
                scope.ConversationId, dense.Count + lexical.Count);

            return EmptyResult(scope, trimmedQuery, Combine(filterNote, "Nothing in the documents matched closely enough. Try different or broader wording."));
        }

        var chunks = await FetchChunksAsync(scope, ExpandToNeighbours(selected), queryVector, cancellationToken).ConfigureAwait(false);
        var passages = BuildPassages(scope, selected, chunks);
        var (budgeted, truncated) = ApplyTokenBudget(passages);

        _logger.LogInformation(
            "Document search for conversation {ConversationId} returned {PassageCount} passage(s) from {ChunkCount} chunk(s).",
            scope.ConversationId, budgeted.Count, chunks.Count);

        return new DocumentSearchResult
        {
            Query = trimmedQuery,
            ResultCount = budgeted.Count,
            Truncated = truncated,
            Results = budgeted,
            AvailableDocuments = budgeted.Count == 0 ? scope.DocumentNames : null,
            Note = Combine(filterNote, budgeted.Count == 0 ? "Nothing in the documents matched closely enough." : null)
        };
    }

    #region Query preparation

    /// <summary>
    /// Turns the query into a vector the database can compare against the stored embeddings.
    /// </summary>
    /// <exception cref="InvalidOperationException">The generator returned nothing, or a vector of the wrong size.</exception>
    private async Task<SqlVector<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        var generated = await _embeddingGenerator.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (generated.Count == 0)
        {
            throw new InvalidOperationException("The embedding generator returned no vector for the search query.");
        }

        var vector = generated[0].Vector;

        if (vector.Length != EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"The embedding generator returned a {vector.Length}-dimension vector, but stored chunks are {EmbeddingDimensions}-dimension. " +
                "The configured embedding deployment does not match the one documents were ingested with.");
        }

        return new SqlVector<float>(vector);
    }

    /// <summary>
    /// Picks the distinct terms worth searching for literally.
    /// </summary>
    /// <param name="query">The raw query.</param>
    /// <param name="maxTerms">The most terms to return.</param>
    /// <returns>The terms, most distinctive first.</returns>
    /// <remarks>
    /// Ordering puts tokens containing digits first and longer tokens next, as a cheap stand-in for
    /// inverse document frequency: identifiers and long words discriminate between chunks, common short
    /// words do not. Two-character tokens survive only when they contain a digit, which keeps <c>v2</c>
    /// and <c>5g</c> while dropping the noise a two-letter word would add.
    /// </remarks>
    internal static IReadOnlyList<string> ExtractTerms(string query, int maxTerms)
    {
        if (string.IsNullOrWhiteSpace(query) || maxTerms <= 0)
        {
            return [];
        }

        List<string> tokens = [];
        var current = new StringBuilder();

        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }

            AddToken(tokens, current);
        }

        AddToken(tokens, current);

        return [.. tokens
            .Distinct(StringComparer.Ordinal)
            .Where(t => !StopWords.Contains(t))
            .OrderByDescending(t => t.Any(char.IsDigit))
            .ThenByDescending(t => t.Length)
            .ThenBy(t => t, StringComparer.Ordinal)
            .Take(maxTerms)];

        static void AddToken(List<string> tokens, StringBuilder current)
        {
            if (current.Length == 0)
            {
                return;
            }

            var token = current.ToString();
            current.Clear();

            var keep = token.Length >= 3 || (token.Length >= 2 && token.Any(char.IsDigit));
            if (keep)
            {
                tokens.Add(token);
            }
        }
    }

    /// <summary>
    /// Escapes the characters <c>LIKE</c> treats as wildcards so a term matches itself literally.
    /// </summary>
    /// <param name="term">The term to escape.</param>
    /// <returns>The escaped term, for use with <c>ESCAPE '\'</c>.</returns>
    /// <remarks>
    /// The backslash is escaped first by virtue of being in the same pass — it is the escape character
    /// itself, so a term containing one would otherwise consume the character after it.
    /// </remarks>
    internal static string EscapeLikePattern(string term)
    {
        var escaped = new StringBuilder(term.Length + 8);

        foreach (var character in term)
        {
            if (character is '\\' or '%' or '_' or '[' or ']')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Finds the documents in scope whose name matches <paramref name="wanted"/>, widening the
    /// comparison only as far as it has to.
    /// </summary>
    /// <param name="scope">The documents to match against.</param>
    /// <param name="wanted">The name to match, already trimmed.</param>
    /// <returns>
    /// Every match at the narrowest rung that produced any: an exact name, else a prefix, else a
    /// substring. Empty when nothing matches at all.
    /// </returns>
    /// <remarks>
    /// Shared rather than duplicated because the two tools that take a document name must agree on
    /// what a name means; they deliberately disagree on what to do about an ambiguous one, which is
    /// why this returns the matches instead of a decision.
    /// </remarks>
    internal static IReadOnlyList<RetrievableDocument> MatchByName(DocumentRetrievalScope scope, string wanted)
    {
        ArgumentNullException.ThrowIfNull(scope);

        List<RetrievableDocument> matches =
            [.. scope.Documents.Where(d => string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase))];

        if (matches.Count == 0)
        {
            matches = [.. scope.Documents.Where(d => d.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))];
        }

        if (matches.Count == 0)
        {
            matches = [.. scope.Documents.Where(d => d.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
        }

        return matches;
    }

    /// <summary>
    /// Maps a caller-supplied document name onto a document to restrict a <em>search</em> to.
    /// </summary>
    /// <returns>
    /// The document to restrict to and a note for the model, either of which may be
    /// <see langword="null"/>. An unknown or ambiguous name searches everything rather than nothing:
    /// silently returning no results for a near-miss on a file name reads to the model as "the documents
    /// do not say", which is a worse failure than a wider search with an explanation attached. Summarizing
    /// takes the opposite decision on the same matches, because a wider search is cheap and summarizing
    /// the wrong document is not.
    /// </returns>
    private static (Guid? DocumentId, string? Note) ResolveDocumentFilter(DocumentRetrievalScope scope, string? documentName)
    {
        if (string.IsNullOrWhiteSpace(documentName))
        {
            return (null, null);
        }

        var wanted = documentName.Trim();
        var matches = MatchByName(scope, wanted);

        return matches.Count switch
        {
            1 => (matches[0].Id, null),
            0 => (null, $"No document is named '{wanted}'; searched all documents instead. Available: {string.Join(", ", scope.DocumentNames)}."),
            _ => (null, $"'{wanted}' matches more than one document; searched all documents instead. Available: {string.Join(", ", scope.DocumentNames)}.")
        };
    }

    #endregion

    #region Ranking

    /// <summary>
    /// Fuses the two passes, drops what is not relevant enough, and caps how much any one document can
    /// contribute.
    /// </summary>
    /// <param name="dense">The vector pass, closest first.</param>
    /// <param name="lexical">The keyword pass, best first.</param>
    /// <param name="termCount">How many terms the keyword pass searched for.</param>
    /// <returns>The chunks worth showing, most relevant first.</returns>
    internal IReadOnlyList<ScoredChunk> SelectHits(
        IReadOnlyList<ChunkCandidate> dense,
        IReadOnlyList<ChunkCandidate> lexical,
        int termCount)
    {
        var fused = new Dictionary<ChunkKey, ScoredChunk>();

        Fuse(fused, dense);
        Fuse(fused, lexical);

        // A vector match is trusted on distance alone; a keyword-only match has no distance yet, so it
        // has to earn its place on term coverage. One matching term out of several is usually the query's
        // most common word appearing somewhere irrelevant.
        var requiredTerms = Math.Min(2, Math.Max(termCount, 1));

        var ranked = fused.Values
            .Where(c => (c.Distance.HasValue && c.Distance.Value <= _options.MaxDistance) || c.MatchCount >= requiredTerms)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Distance ?? double.MaxValue)
            .ThenBy(c => c.Key.Index);

        var perDocument = new Dictionary<Guid, int>();
        var selected = new List<ScoredChunk>(_options.MaxResults);

        foreach (var candidate in ranked)
        {
            perDocument.TryGetValue(candidate.Key.DocumentId, out var taken);
            if (taken >= _options.MaxPassagesPerDocument)
            {
                continue;
            }

            perDocument[candidate.Key.DocumentId] = taken + 1;
            selected.Add(candidate);

            if (selected.Count == _options.MaxResults)
            {
                break;
            }
        }

        return selected;

        static void Fuse(Dictionary<ChunkKey, ScoredChunk> fused, IReadOnlyList<ChunkCandidate> pass)
        {
            for (var i = 0; i < pass.Count; i++)
            {
                var candidate = pass[i];
                var key = new ChunkKey(candidate.DocumentId, candidate.Source, candidate.Index);
                var contribution = 1.0 / (RrfK + i + 1);

                fused[key] = fused.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        Score = existing.Score + contribution,
                        Distance = existing.Distance ?? candidate.Distance,
                        MatchCount = Math.Max(existing.MatchCount, candidate.MatchCount)
                    }
                    : new ScoredChunk(key, contribution, candidate.Distance, candidate.MatchCount);
            }
        }
    }

    /// <summary>
    /// Widens each match into the chunk indices around it.
    /// </summary>
    /// <param name="hits">The selected chunks.</param>
    /// <returns>The distinct chunk keys to read, matches and neighbours alike.</returns>
    /// <remarks>
    /// De-duplicated here rather than in SQL so overlapping windows in the same document cost one seek
    /// instead of several, and so the read-back never has to sort <c>nvarchar(max)</c> to remove
    /// duplicate rows. Indices past the end of a document simply fail to join.
    /// </remarks>
    internal IReadOnlyList<ChunkKey> ExpandToNeighbours(IReadOnlyList<ScoredChunk> hits)
    {
        var window = _options.NeighborWindow;
        var wanted = new HashSet<ChunkKey>();

        foreach (var hit in hits)
        {
            for (var offset = -window; offset <= window; offset++)
            {
                var index = hit.Key.Index + offset;
                if (index >= 0)
                {
                    wanted.Add(hit.Key with { Index = index });
                }
            }
        }

        return [.. wanted];
    }

    #endregion

    #region Passage assembly

    /// <summary>
    /// Merges the read-back chunks into contiguous passages, one per unbroken run of chunk indices.
    /// </summary>
    /// <param name="scope">The corpus, used to resolve document names for citations.</param>
    /// <param name="hits">The chunks that actually matched, which is what a passage is scored on.</param>
    /// <param name="chunks">The chunks read back, matches and neighbours together.</param>
    /// <returns>The passages, most relevant first.</returns>
    /// <remarks>
    /// Ordered by the fused rank-fusion score, not by cosine distance. Ordering by distance would put
    /// every keyword-only match last by construction — such a match is only in the result set because its
    /// distance failed the gate — so the exact-token hits the keyword pass exists to find would be ranked
    /// least relevant and dropped first by the token budget.
    /// </remarks>
    internal IReadOnlyList<RetrievedPassage> BuildPassages(
        DocumentRetrievalScope scope,
        IReadOnlyList<ScoredChunk> hits,
        IReadOnlyList<ChunkText> chunks)
    {
        var names = scope.Documents.ToDictionary(d => d.Id, d => d.Name);

        // DistinctBy so this stays total: the production caller passes dictionary values, whose keys are
        // unique by construction, but this is internal and tests hand it arrays directly.
        var hitsByKey = hits.DistinctBy(h => h.Key).ToDictionary(h => h.Key);
        var maxOverlapChars = Math.Max(64, _textChunker.OverlapTokens * 6);

        List<(double Relevance, double Distance, RetrievedPassage Passage)> passages = [];

        foreach (var group in chunks.GroupBy(c => (c.Key.DocumentId, c.Key.Source)))
        {
            var ordered = group.OrderBy(c => c.Key.Index).ToList();
            List<ChunkText> run = [];

            foreach (var chunk in ordered)
            {
                if (run.Count > 0 && chunk.Key.Index != run[^1].Key.Index + 1)
                {
                    AddPassage(run);
                    run = [];
                }

                run.Add(chunk);
            }

            AddPassage(run);

            void AddPassage(List<ChunkText> members)
            {
                if (members.Count == 0)
                {
                    return;
                }

                // Scored on the matches only. A neighbour was pulled in for context, and letting one
                // score the passage would rank a run by an accident of where the chunker cut.
                List<(ChunkText Chunk, ScoredChunk Hit)> matched = [];
                foreach (var member in members)
                {
                    if (hitsByKey.TryGetValue(member.Key, out var hit))
                    {
                        matched.Add((member, hit));
                    }
                }

                if (matched.Count == 0)
                {
                    return;
                }

                var text = new StringBuilder(members[0].Text);
                for (var i = 1; i < members.Count; i++)
                {
                    AppendWithoutOverlap(text, members[i].Text, maxOverlapChars);
                }

                var relevance = matched.Max(m => m.Hit.Score);
                var distance = matched.Min(m => m.Chunk.Distance);
                var documentName = names.TryGetValue(group.Key.DocumentId, out var name) ? name : "document";

                // The best match's page, not the run's first: a match at the top of page 5 widened by a
                // neighbour that began on page 4 would otherwise be cited as page 4, and the model is
                // told to quote the citation verbatim.
                var page = matched
                    .OrderBy(m => m.Chunk.Distance)
                    .Select(m => m.Chunk.SourceNumber)
                    .FirstOrDefault(p => p.HasValue)
                    ?? members.Select(m => m.SourceNumber).FirstOrDefault(p => p.HasValue);

                passages.Add((relevance, distance, new RetrievedPassage
                {
                    Citation = page.HasValue
                        ? string.Create(CultureInfo.InvariantCulture, $"{documentName} p.{page.Value}")
                        : documentName,
                    DocumentName = documentName,
                    Page = page,
                    Score = Math.Round(Math.Clamp(1.0 - distance, 0.0, 1.0), 3),
                    Text = text.ToString()
                }));
            }
        }

        return
        [
            .. passages
                .OrderByDescending(p => p.Relevance)
                .ThenBy(p => p.Distance)
                .Select(p => p.Passage)
        ];
    }

    /// <summary>
    /// Appends <paramref name="next"/> to <paramref name="accumulated"/>, dropping the text the two
    /// already share at the seam.
    /// </summary>
    /// <param name="accumulated">The passage built so far; appended to in place.</param>
    /// <param name="next">The following chunk's text.</param>
    /// <param name="maxOverlapChars">The longest seam to look for.</param>
    /// <remarks>
    /// Consecutive chunks are built to overlap: the chunker seeds each one with the tail of the last, so
    /// the head of <paramref name="next"/> is character-for-character the tail of
    /// <paramref name="accumulated"/>. Finding that seam and skipping it is what stops a three-chunk
    /// passage from repeating a quarter of itself twice. The longest seam is preferred so a repeated
    /// short phrase inside the real overlap cannot cut it short, and a run with no seam at all — which a
    /// re-sliced oversized chunk can produce — falls back to a paragraph break.
    /// </remarks>
    internal static void AppendWithoutOverlap(StringBuilder accumulated, string next, int maxOverlapChars)
    {
        if (next.Length == 0)
        {
            return;
        }

        if (accumulated.Length == 0)
        {
            accumulated.Append(next);
            return;
        }

        var tail = accumulated.ToString(
            Math.Max(0, accumulated.Length - maxOverlapChars),
            Math.Min(maxOverlapChars, accumulated.Length));

        var longest = Math.Min(tail.Length, next.Length);

        for (var length = longest; length >= MinOverlapChars; length--)
        {
            if (string.CompareOrdinal(tail, tail.Length - length, next, 0, length) == 0)
            {
                accumulated.Append(next, length, next.Length - length);
                return;
            }
        }

        accumulated.Append("\n\n").Append(next);
    }

    /// <summary>
    /// Trims the passage list to the configured token budget.
    /// </summary>
    /// <param name="passages">The passages, most relevant first.</param>
    /// <returns>The passages that fit, and whether any were dropped.</returns>
    /// <remarks>
    /// The first passage is always kept even if it alone exceeds the budget: returning nothing because
    /// the single best match is long is worse than overshooting once. A merged passage is bounded by
    /// the chunk size times the neighbour window, so the overshoot is bounded too.
    /// </remarks>
    internal (IReadOnlyList<RetrievedPassage> Passages, bool Truncated) ApplyTokenBudget(IReadOnlyList<RetrievedPassage> passages)
    {
        var kept = new List<RetrievedPassage>(passages.Count);
        var used = 0;

        foreach (var passage in passages)
        {
            var tokens = _textChunker.CountTokens(passage.Text);

            if (kept.Count > 0 && used + tokens > _options.MaxResultTokens)
            {
                return (kept, true);
            }

            kept.Add(passage);
            used += tokens;
        }

        return (kept, false);
    }

    #endregion

    #region Data access

    private async Task<IReadOnlyList<ChunkCandidate>> RunDenseSearchAsync(
        DocumentRetrievalScope scope,
        Guid? documentId,
        SqlVector<float> queryVector,
        CancellationToken cancellationToken)
    {
        return await QueryAsync(
            DocumentRetrievalSql.DenseSearch,
            command =>
            {
                AddScopeParameters(command, scope, documentId);
                command.Parameters.Add(new SqlParameter("@queryVector", queryVector));
                command.Parameters.Add(new SqlParameter("@candidates", SqlDbType.Int) { Value = _options.CandidateCount });
            },
            reader => new ChunkCandidate(
                reader.GetGuid(0),
                (DocumentSource)reader.GetByte(1),
                reader.GetInt32(2),
                reader.GetDouble(3),
                MatchCount: 0),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChunkCandidate>> RunLexicalSearchAsync(
        DocumentRetrievalScope scope,
        Guid? documentId,
        IReadOnlyList<string> terms,
        CancellationToken cancellationToken)
    {
        return await QueryAsync(
            DocumentRetrievalSql.BuildLexicalSearch(terms.Count),
            command =>
            {
                AddScopeParameters(command, scope, documentId);
                command.Parameters.Add(new SqlParameter("@lexicalCandidates", SqlDbType.Int) { Value = _options.LexicalCandidateCount });

                for (var i = 0; i < terms.Count; i++)
                {
                    command.Parameters.Add(new SqlParameter($"{DocumentRetrievalSql.TermParameterPrefix}{i}", SqlDbType.NVarChar, 4000)
                    {
                        Value = $"%{EscapeLikePattern(terms[i])}%"
                    });
                }
            },
            reader => new ChunkCandidate(
                reader.GetGuid(0),
                (DocumentSource)reader.GetByte(1),
                reader.GetInt32(2),
                Distance: null,
                reader.GetInt32(3)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChunkText>> FetchChunksAsync(
        DocumentRetrievalScope scope,
        IReadOnlyList<ChunkKey> keys,
        SqlVector<float> queryVector,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        return await QueryAsync(
            DocumentRetrievalSql.BuildFetchChunks(keys.Count),
            command =>
            {
                command.Parameters.Add(new SqlParameter("@conversationId", SqlDbType.UniqueIdentifier) { Value = scope.ConversationId });
                command.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = (object?)scope.ProjectId ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@queryVector", queryVector));

                for (var i = 0; i < keys.Count; i++)
                {
                    command.Parameters.Add(new SqlParameter($"{DocumentRetrievalSql.DocumentParameterPrefix}{i}", SqlDbType.UniqueIdentifier) { Value = keys[i].DocumentId });
                    command.Parameters.Add(new SqlParameter($"{DocumentRetrievalSql.SourceParameterPrefix}{i}", SqlDbType.TinyInt) { Value = (byte)keys[i].Source });
                    command.Parameters.Add(new SqlParameter($"{DocumentRetrievalSql.IndexParameterPrefix}{i}", SqlDbType.Int) { Value = keys[i].Index });
                }
            },
            reader => new ChunkText(
                new ChunkKey(reader.GetGuid(0), (DocumentSource)reader.GetByte(1), reader.GetInt32(2)),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetString(4),
                reader.GetDouble(5)),
            cancellationToken).ConfigureAwait(false);
    }

    private static void AddScopeParameters(DbCommand command, DocumentRetrievalScope scope, Guid? documentId)
    {
        command.Parameters.Add(new SqlParameter("@conversationId", SqlDbType.UniqueIdentifier) { Value = scope.ConversationId });
        command.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = (object?)scope.ProjectId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@documentId", SqlDbType.UniqueIdentifier) { Value = (object?)documentId ?? DBNull.Value });
    }

    /// <summary>
    /// Runs one retrieval statement on the context's own connection.
    /// </summary>
    /// <remarks>
    /// Dropped to ADO.NET rather than <c>SqlQueryRaw</c> because EF composes unmapped-type queries into a
    /// wrapping subquery, which these statements — a common table expression, and a <c>TOP</c> ordered by
    /// a computed distance — cannot survive. The connection, and any ambient transaction, still belong to
    /// EF: only a connection this method opened is closed again, so a caller already inside a transaction
    /// keeps it.
    /// </remarks>
    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Action<DbCommand> configure,
        Func<DbDataReader, T> read,
        CancellationToken cancellationToken)
    {
        var connection = _ctx.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _options.CommandTimeoutSeconds;
            command.Transaction = _ctx.Database.CurrentTransaction?.GetDbTransaction();
            configure(command);

            var results = new List<T>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(read(reader));
            }

            return results;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    #endregion

    private static DocumentSearchResult EmptyResult(DocumentRetrievalScope scope, string query, string? note) => new()
    {
        Query = query,
        ResultCount = 0,
        Truncated = false,
        Results = [],
        AvailableDocuments = scope.DocumentNames,
        Note = note
    };

    private static string? Combine(string? first, string? second) =>
        (first, second) switch
        {
            (null or "", null or "") => null,
            (null or "", _) => second,
            (_, null or "") => first,
            _ => $"{first} {second}"
        };
}

/// <summary>
/// A chunk with its fused rank-fusion score and the evidence each pass contributed.
/// </summary>
/// <param name="Key">Which chunk this is.</param>
/// <param name="Score">The Reciprocal Rank Fusion score across both passes; higher is better.</param>
/// <param name="Distance">The cosine distance, or <see langword="null"/> when only the keyword pass found it.</param>
/// <param name="MatchCount">The number of query terms found in the chunk; zero when only the vector pass found it.</param>
internal readonly record struct ScoredChunk(ChunkKey Key, double Score, double? Distance, int MatchCount);
