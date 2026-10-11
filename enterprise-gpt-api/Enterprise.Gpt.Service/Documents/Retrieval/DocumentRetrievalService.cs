using System.Collections.Frozen;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Enterprise.Gpt.Common.Constants.Documents;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Common.Extensions;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Documents.Chunking;
using Enterprise.Gpt.Service.Documents.Retrieval.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents.Retrieval;

/// <summary>
/// Searches the documents attached to a conversation, and to its project, for the passages most
/// relevant to a query.
/// </summary>
public interface IDocumentRetrievalService
{
    /// <summary>
    /// Resolves what a conversation's retrieval tool is allowed to search.
    /// </summary>
    /// <remarks>
    /// The project comes off the conversation row, never the caller, and a deactivated project is dropped
    /// from the scope even though the conversation still points at it.
    /// </remarks>
    Task<DocumentRetrievalScope> GetScopeAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the passages in <paramref name="scope"/> (optionally one named document) that best answer
    /// <paramref name="query"/>.
    /// </summary>
    /// <returns>
    /// Passages, most relevant first. An empty result is normal and names the documents searched.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    Task<DocumentSearchResult> SearchAsync(DocumentRetrievalScope scope, string query, string? documentName, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// Vector and keyword passes are fused with Reciprocal Rank Fusion (embeddings miss codes and surnames,
/// keywords miss paraphrase). Matches widen to their neighbours and contiguous runs merge minus the
/// overlapping seam, so the model reads whole passages and pays for each sentence once.
/// </remarks>
public sealed partial class DocumentRetrievalService(
    ILogger<DocumentRetrievalService> logger,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ITextChunker textChunker,
    IOptions<DocumentRetrievalOptions> options,
    EnterpriseGptDbContext ctx) : IDocumentRetrievalService
{
    // From the RRF paper: damps the top ranks so a chunk both passes agree on beats one either ranks first.
    private const double RrfK = 60.0;

    // Shorter seams are as likely a coincidence (a shared "the ") as real overlap.
    private const int MinOverlapChars = 16;

    // Far beyond a real query; stops a runaway generation becoming a large embedding request.
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

    // Chunks of these carry a sheet ordinal, not a page. Not derived from the extractors, to keep citation
    // off their construction path; DocumentRetrievalServiceTests catches drift.
    private static readonly FileExtensions[] _sheetFormats = [FileExtensions.Xlsx, FileExtensions.Csv];

    internal static readonly FrozenSet<string> SpreadsheetExtensions =
        _sheetFormats.Select(x => x.GetDescription()).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<DocumentRetrievalService> _logger = logger;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = embeddingGenerator;
    private readonly ITextChunker _textChunker = textChunker;
    private readonly DocumentRetrievalOptions _options = options.Value;
    private readonly EnterpriseGptDbContext _ctx = ctx;

    /// <inheritdoc />
    public async Task<DocumentRetrievalScope> GetScopeAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        List<RetrievableDocument> documents = [];

        // Uploaded only: generated documents have no chunks, and excluding them here also keeps their
        // names out of the prompt, HasDocuments and every name lookup through this scope.
        var conversationDocuments = await _ctx.ConversationDocuments
            .AsNoTracking()
            .Where(d => d.ConversationId == conversationId
                && d.Type == ConversationDocumentTypes.Uploaded)
            .OrderBy(d => d.DateCreated)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        documents.AddRange(conversationDocuments.Select(d => new RetrievableDocument(d.Id, DocumentSources.Conversation, d.Name)));

        // Off the conversation, so a project is reachable only through its own conversations. The soft-delete
        // filter on the navigation makes a deactivated project read as none.
        var activeProjectId = await _ctx.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId && c.Project != null)
            .Select(c => c.ProjectId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeProjectId.HasValue)
        {
            var projectDocuments = await _ctx.ProjectDocuments
                .AsNoTracking()
                .Where(d => d.ProjectId == activeProjectId.Value)
                .OrderBy(d => d.DateCreated)
                .Select(d => new { d.Id, d.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            documents.AddRange(projectDocuments.Select(d => new RetrievableDocument(d.Id, DocumentSources.Project, d.Name)));
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

        // Model-supplied and sent to a billed embedding deployment; anything this long is a runaway generation.
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
            LogSearchMatchedNothing(_logger, scope.ConversationId, dense.Count + lexical.Count);

            return EmptyResult(scope, trimmedQuery, Combine(filterNote, "Nothing in the documents matched closely enough. Try different or broader wording."));
        }

        var chunks = await FetchChunksAsync(scope, ExpandToNeighbours(selected), queryVector, cancellationToken).ConfigureAwait(false);
        var sheetNames = await ResolveSheetNamesAsync(scope, chunks, cancellationToken).ConfigureAwait(false);
        var passages = BuildPassages(scope, selected, chunks, sheetNames);
        var (budgeted, truncated) = ApplyTokenBudget(passages);

        LogSearchReturned(_logger, scope.ConversationId, budgeted.Count, chunks.Count);

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

    /// <summary>
    /// Fuses the two passes, drops what is not relevant enough, and caps how much any one document can
    /// contribute.
    /// </summary>
    internal IReadOnlyList<ScoredChunk> SelectHits(
        IReadOnlyList<ChunkCandidate> dense,
        IReadOnlyList<ChunkCandidate> lexical,
        int termCount)
    {
        Dictionary<ChunkKey, ScoredChunk> fused = [];

        Fuse(fused, dense);
        Fuse(fused, lexical);

        // Keyword-only matches have no distance, so they qualify on term coverage; one term of several is
        // usually a common word appearing somewhere irrelevant.
        var requiredTerms = Math.Min(2, Math.Max(termCount, 1));

        var ranked = fused.Values
            .Where(c => (c.Distance.HasValue && c.Distance.Value <= _options.MaxDistance) || c.MatchCount >= requiredTerms)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Distance ?? double.MaxValue)
            .ThenBy(c => c.Key.Index);

        Dictionary<Guid, int> perDocument = [];
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

    // De-duplicated here, not in SQL, so overlapping windows cost one seek and the read-back never sorts
    // nvarchar(max). Indices past a document's end simply fail to join.
    internal IReadOnlyList<ChunkKey> ExpandToNeighbours(IReadOnlyList<ScoredChunk> hits)
    {
        var window = _options.NeighborWindow;
        HashSet<ChunkKey> wanted = [];

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

    /// <summary>
    /// Merges the read-back chunks into contiguous passages, one per unbroken run of chunk indices.
    /// </summary>
    /// <remarks>
    /// Ordered by fused score, not distance: keyword-only matches failed the distance gate, so distance
    /// order would rank them last and the token budget would drop them first.
    /// </remarks>
    internal IReadOnlyList<RetrievedPassage> BuildPassages(
        DocumentRetrievalScope scope,
        IReadOnlyList<ScoredChunk> hits,
        IReadOnlyList<ChunkText> chunks,
        IReadOnlyDictionary<(Guid DocumentId, int SheetIndex), string>? sheetNames = null)
    {
        var names = scope.Documents.ToDictionary(d => d.Id, d => d.Name);

        // DistinctBy keeps this total for tests that pass arrays; production keys are already unique.
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

                // Scored on matches only; a context neighbour would rank the run by where the chunker cut.
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

                // The best match's page, not the run's first, which may be a neighbour's earlier page; the
                // model quotes the citation verbatim.
                var page = matched
                    .OrderBy(m => m.Chunk.Distance)
                    .Select(m => m.Chunk.SourceNumber)
                    .FirstOrDefault(p => p.HasValue)
                    ?? members.Select(m => m.SourceNumber).FirstOrDefault(p => p.HasValue);

                var sheetName = page is { } sheetIndex && sheetNames is not null
                    && sheetNames.TryGetValue((group.Key.DocumentId, sheetIndex), out var resolved)
                        ? resolved
                        : null;

                passages.Add((relevance, distance, new RetrievedPassage
                {
                    Citation = BuildCitation(documentName, page, sheetName),
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

    // The first passage is always kept: overshooting once (bounded by chunk size times the neighbour
    // window) beats returning nothing.
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

    #region Private methods

    private async Task<SqlVector<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        var generated = await _embeddingGenerator.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (generated.Count == 0)
        {
            throw new InvalidOperationException("The embedding generator returned no vector for the search query.");
        }

        var vector = generated[0].Vector;

        if (vector.Length != DocumentChunkLimits.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"The embedding generator returned a {vector.Length}-dimension vector, but stored chunks are {DocumentChunkLimits.EmbeddingDimensions}-dimension. " +
                "The configured embedding deployment does not match the one documents were ingested with.");
        }

        return new SqlVector<float>(vector);
    }

    // An unknown or ambiguous name searches everything, with a note: an empty result reads to the model as
    // "the documents do not say". Summarizing decides the opposite, as summarizing the wrong file is costly.
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

    // Separate from the retrieval statements so a corpus without spreadsheets never queries for this.
    private async Task<IReadOnlyDictionary<(Guid DocumentId, int SheetIndex), string>?> ResolveSheetNamesAsync(
        DocumentRetrievalScope scope,
        IReadOnlyList<ChunkText> chunks,
        CancellationToken cancellationToken)
    {
        var spreadsheets = scope.Documents
            .Where(d => IsSpreadsheet(d.Name))
            .Select(d => d.Id)
            .ToHashSet();

        if (spreadsheets.Count == 0)
        {
            return null;
        }

        HashSet<Guid> conversationDocumentIds = [];
        HashSet<Guid> projectDocumentIds = [];

        foreach (var chunk in chunks)
        {
            if (!spreadsheets.Contains(chunk.Key.DocumentId))
            {
                continue;
            }

            if (chunk.Key.Source == DocumentSources.Conversation)
            {
                conversationDocumentIds.Add(chunk.Key.DocumentId);
            }
            else
            {
                projectDocumentIds.Add(chunk.Key.DocumentId);
            }
        }

        Dictionary<(Guid DocumentId, int SheetIndex), string> names = [];

        if (conversationDocumentIds.Count > 0)
        {
            var sheets = await _ctx.ConversationDocumentSheets
                .AsNoTracking()
                .Where(s => conversationDocumentIds.Contains(s.ConversationDocumentId))
                .Select(s => new { s.ConversationDocumentId, s.SheetIndex, s.SheetName })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var sheet in sheets)
            {
                names[(sheet.ConversationDocumentId, sheet.SheetIndex)] = sheet.SheetName;
            }
        }

        if (projectDocumentIds.Count > 0)
        {
            var sheets = await _ctx.ProjectDocumentSheets
                .AsNoTracking()
                .Where(s => projectDocumentIds.Contains(s.ProjectDocumentId))
                .Select(s => new { s.ProjectDocumentId, s.SheetIndex, s.SheetName })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var sheet in sheets)
            {
                names[(sheet.ProjectDocumentId, sheet.SheetIndex)] = sheet.SheetName;
            }
        }

        return names.Count == 0 ? null : names;
    }

    private static bool IsSpreadsheet(string documentName)
    {
        return SpreadsheetExtensions.Contains(Path.GetExtension(documentName));
    }

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
                (DocumentSources)reader.GetByte(1),
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
                (DocumentSources)reader.GetByte(1),
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
                new ChunkKey(reader.GetGuid(0), (DocumentSources)reader.GetByte(1), reader.GetInt32(2)),
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

    // ADO.NET, not SqlQueryRaw: EF wraps unmapped queries in a subquery that a CTE, or a TOP ordered by a
    // computed distance, cannot survive. Closes only a connection it opened, keeping any caller transaction.
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

            List<T> results = [];

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

    #endregion

    #region Public static methods

    /// <summary>
    /// Picks the distinct terms worth searching for literally, most distinctive first.
    /// </summary>
    /// <remarks>
    /// Digits first, then length, as a cheap stand-in for inverse document frequency. Two-character tokens
    /// survive only with a digit (<c>v2</c>, <c>5g</c>).
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
    /// Escapes <c>LIKE</c> wildcards, and the backslash itself, for use with <c>ESCAPE '\'</c>.
    /// </summary>
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
    /// Finds the documents whose name matches the already-trimmed <paramref name="wanted"/> at the narrowest
    /// rung that produces any: exact, else prefix, else substring.
    /// </summary>
    /// <remarks>
    /// Both name-taking tools share this so they agree on matching; they deliberately differ on ambiguity,
    /// hence matches rather than a decision.
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
    /// Builds the reference the model is told to quote verbatim.
    /// </summary>
    /// <remarks>
    /// A spreadsheet's source number is a sheet ordinal, never a page; without a stored sheet name the file
    /// name stands alone.
    /// </remarks>
    internal static string BuildCitation(string documentName, int? page, string? sheetName)
    {
        if (!IsSpreadsheet(documentName))
        {
            return page is { } number
                ? string.Create(CultureInfo.InvariantCulture, $"{documentName} p.{number}")
                : documentName;
        }

        return sheetName is null
            ? documentName
            : string.Create(CultureInfo.InvariantCulture, $"{documentName} — {sheetName}");
    }

    /// <summary>
    /// Appends <paramref name="next"/> to <paramref name="accumulated"/>, dropping the text the two
    /// already share at the seam.
    /// </summary>
    /// <remarks>
    /// The chunker seeds each chunk with the previous tail, so the seam is exact. The longest seam wins so a
    /// repeated phrase cannot cut it short; no seam (a re-sliced chunk) falls back to a paragraph break.
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

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Document search for conversation {ConversationId} matched none of {ChunkCount} candidate chunk(s).")]
    private static partial void LogSearchMatchedNothing(ILogger logger, Guid conversationId, int chunkCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Document search for conversation {ConversationId} returned {PassageCount} passage(s) from {ChunkCount} chunk(s).")]
    private static partial void LogSearchReturned(
        ILogger logger, Guid conversationId, int passageCount, int chunkCount);

    #endregion
}
