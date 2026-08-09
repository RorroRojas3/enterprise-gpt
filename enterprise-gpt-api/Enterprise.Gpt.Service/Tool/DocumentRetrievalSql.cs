using System.Text;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// A chunk that matched one retrieval pass, before fusion.
/// </summary>
/// <param name="DocumentId">The document the chunk belongs to.</param>
/// <param name="Source">Which table the chunk lives in.</param>
/// <param name="Index">The chunk's zero-based ordinal within its document.</param>
/// <param name="Distance">The cosine distance from the query vector, or <see langword="null"/> for a keyword match.</param>
/// <param name="MatchCount">The number of query terms present in the chunk, or zero for a vector match.</param>
internal readonly record struct ChunkCandidate(
    Guid DocumentId,
    DocumentSource Source,
    int Index,
    double? Distance,
    int MatchCount);

/// <summary>
/// Identifies one chunk within the searchable corpus.
/// </summary>
/// <param name="DocumentId">The document the chunk belongs to.</param>
/// <param name="Source">Which table the chunk lives in.</param>
/// <param name="Index">The chunk's zero-based ordinal within its document.</param>
internal readonly record struct ChunkKey(Guid DocumentId, DocumentSource Source, int Index);

/// <summary>
/// A chunk's text and provenance, read back once retrieval has decided it is worth showing.
/// </summary>
/// <param name="Key">Which chunk this is.</param>
/// <param name="SourceNumber">The page or slide the chunk starts on, when the format has one.</param>
/// <param name="Text">The chunk text.</param>
/// <param name="Distance">
/// The chunk's own cosine distance from the query vector. Recomputed on read-back rather than carried
/// from the pass that selected it, so a chunk found only by keyword — and a neighbour pulled in for
/// context — still has a comparable score.
/// </param>
/// <remarks>
/// The stored <c>TokenCount</c> is deliberately not read: merging strips the overlap between chunks, so
/// a passage is shorter than the sum of its parts and the budget has to measure the merged text.
/// </remarks>
internal sealed record ChunkText(ChunkKey Key, int? SourceNumber, string Text, double Distance);

/// <summary>
/// The hand-written T-SQL behind document retrieval.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than as LINQ for two reasons that both cost real time per turn. EF Core 10
/// materializes <c>SqlVector&lt;float&gt;</c> like any other property, so a LINQ query over the chunk
/// entities would drag 6 KB of embedding per candidate row across the wire to reach a distance it never
/// keeps; every statement here mentions <c>Embedding</c> only inside <c>VECTOR_DISTANCE</c>. And the
/// candidate set is a <c>UNION ALL</c> of two structurally identical tables projected to one shape,
/// which EF cannot express without materializing both sides.
/// </para>
/// <para>
/// Search is exact k-nearest-neighbour, with no vector index, and deliberately so. The candidate set is
/// always narrowed to one conversation — and at most one project — before any distance is computed,
/// which keeps it orders of magnitude below the point where approximate search pays for itself.
/// Approximate search is also still preview on Azure SQL Database, where a vector index has made its
/// table read-only and the <c>ALLOW_STALE_VECTOR_INDEX</c> escape hatch stops maintaining the index —
/// either of which breaks document upload. Newer index versions relax that, so this is worth
/// re-evaluating when it reaches general availability; see <c>docs/documents/retrieval.md</c>. As it
/// stands, nothing here needs the <c>PREVIEW_FEATURES</c> database-scoped configuration.
/// </para>
/// <para>
/// <c>Index</c> is a reserved word and is always bracketed. Column names match property names exactly —
/// there is no <c>HasColumnName</c> anywhere in the repository — and the tables live in the <c>Core</c>
/// schema. Soft delete has no query filter in this model, so every level is filtered by hand: the chunk,
/// its document, and the conversation or project that owns it.
/// </para>
/// </remarks>
internal static class DocumentRetrievalSql
{
    /// <summary>
    /// Prefix for the generated keyword-term parameters.
    /// </summary>
    internal const string TermParameterPrefix = "@t";

    /// <summary>
    /// Prefixes for the generated chunk-key parameters of <see cref="BuildFetchChunks"/>.
    /// </summary>
    internal const string DocumentParameterPrefix = "@d";
    internal const string SourceParameterPrefix = "@s";
    internal const string IndexParameterPrefix = "@i";

    /// <summary>
    /// The searchable chunk set for one conversation, as a derived table aliased <c>c</c>.
    /// </summary>
    /// <remarks>
    /// The project half is gated on <c>@projectId IS NOT NULL</c> so a standalone conversation skips it
    /// entirely rather than joining on a null. <c>Text</c> and <c>Embedding</c> are listed here but only
    /// read when the enclosing statement actually projects them.
    /// </remarks>
    private const string ScopedChunks = """
        (
            SELECT ch.[ConversationDocumentId] AS [DocumentId], CAST(0 AS tinyint) AS [Source],
                   ch.[Index], ch.[TokenCount], ch.[Text], ch.[Embedding]
            FROM [Core].[ConversationDocumentChunk] AS ch
            INNER JOIN [Core].[ConversationDocument] AS cd ON cd.[Id] = ch.[ConversationDocumentId]
            WHERE cd.[ConversationId] = @conversationId
              AND cd.[DateDeactivated] IS NULL
              AND ch.[DateDeactivated] IS NULL
              AND (@documentId IS NULL OR cd.[Id] = @documentId)
            UNION ALL
            SELECT ph.[ProjectDocumentId], CAST(1 AS tinyint),
                   ph.[Index], ph.[TokenCount], ph.[Text], ph.[Embedding]
            FROM [Core].[ProjectDocumentChunk] AS ph
            INNER JOIN [Core].[ProjectDocument] AS pd ON pd.[Id] = ph.[ProjectDocumentId]
            WHERE @projectId IS NOT NULL
              AND pd.[ProjectId] = @projectId
              AND pd.[DateDeactivated] IS NULL
              AND ph.[DateDeactivated] IS NULL
              AND (@documentId IS NULL OR pd.[Id] = @documentId)
        ) AS c
        """;

    /// <summary>
    /// Exact k-nearest-neighbour search over the scoped chunks, closest first.
    /// </summary>
    /// <remarks>
    /// <c>VECTOR_DISTANCE('cosine', …)</c> returns a distance in [0, 2] — smaller is closer — not a
    /// similarity. The rows come back in relevance order, which is what the caller uses as the pass rank.
    /// </remarks>
    internal static string DenseSearch { get; } = $"""
        SELECT TOP (@candidates)
               c.[DocumentId],
               c.[Source],
               c.[Index],
               VECTOR_DISTANCE('cosine', @queryVector, c.[Embedding]) AS [Distance]
        FROM {ScopedChunks}
        ORDER BY [Distance];
        """;

    /// <summary>
    /// Builds the keyword pass: the scoped chunks scored by how many of the query terms they contain,
    /// best first.
    /// </summary>
    /// <param name="termCount">The number of terms, each of which becomes one <c>@tN</c> parameter.</param>
    /// <returns>The command text.</returns>
    /// <remarks>
    /// <para>
    /// Only the <em>number</em> of comparison arms varies with the query; the term values themselves are
    /// never concatenated into the command text. Placeholders are generated from a counter and every
    /// term is bound as a parameter, escaped for <c>LIKE</c> wildcards by the caller.
    /// </para>
    /// <para>
    /// <c>LIKE</c> rather than <c>CONTAINS</c>/<c>FREETEXT</c>: full-text search is a separately
    /// installed component that cannot be assumed present, and over a row set already narrowed to one
    /// conversation this is the same cost class as the vector scan running beside it. The explicit
    /// case- and accent-insensitive collation makes matching behave the same whatever collation the
    /// database was created with, and costs nothing because there is no index on <c>Text</c> to lose.
    /// </para>
    /// <para>
    /// Ties break towards the shorter chunk: when two chunks contain the same terms, the denser one is
    /// the better evidence.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="termCount"/> is not positive.</exception>
    internal static string BuildLexicalSearch(int termCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(termCount);

        var arms = new StringBuilder();
        for (var i = 0; i < termCount; i++)
        {
            arms.Append(i == 0 ? "                     " : "                   + ");
            arms.Append("CASE WHEN c.[Text] COLLATE Latin1_General_CI_AI LIKE ")
                .Append(TermParameterPrefix)
                .Append(i)
                .AppendLine(" ESCAPE '\\' THEN 1 ELSE 0 END");
        }

        return $"""
            SELECT TOP (@lexicalCandidates)
                   m.[DocumentId],
                   m.[Source],
                   m.[Index],
                   m.[MatchCount]
            FROM (
                SELECT c.[DocumentId],
                       c.[Source],
                       c.[Index],
                       c.[TokenCount],
                       (
            {arms.ToString().TrimEnd()}
                       ) AS [MatchCount]
                FROM {ScopedChunks}
            ) AS m
            WHERE m.[MatchCount] > 0
            ORDER BY m.[MatchCount] DESC, m.[TokenCount] ASC;
            """;
    }

    /// <summary>
    /// Builds the read-back of specific chunks by key, including their text.
    /// </summary>
    /// <param name="keyCount">The number of chunk keys, each of which becomes three parameters.</param>
    /// <returns>The command text.</returns>
    /// <remarks>
    /// <para>
    /// The caller has already expanded each match into the neighbouring chunk indices and de-duplicated
    /// the result, so this is a set of exact <c>(DocumentId, Index)</c> seeks rather than a range scan.
    /// Both chunk tables carry a unique filtered index on exactly that pair, which is what makes it one.
    /// </para>
    /// <para>
    /// The ownership predicates are repeated here rather than trusted from the pass that produced the
    /// keys: this statement takes identifiers as parameters, so it re-establishes on its own that every
    /// row it returns belongs to the conversation — or its project — being answered.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keyCount"/> is not positive.</exception>
    internal static string BuildFetchChunks(int keyCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyCount);

        var values = new StringBuilder();
        for (var i = 0; i < keyCount; i++)
        {
            values.Append(i == 0 ? "        " : ",\n        ")
                .Append('(')
                .Append(DocumentParameterPrefix).Append(i).Append(", ")
                .Append(SourceParameterPrefix).Append(i).Append(", ")
                .Append(IndexParameterPrefix).Append(i)
                .Append(')');
        }

        return $"""
            WITH wanted([DocumentId], [Source], [Index]) AS (
                SELECT * FROM (VALUES
            {values}
                ) AS v([DocumentId], [Source], [Index])
            )
            SELECT ch.[ConversationDocumentId] AS [DocumentId],
                   CAST(0 AS tinyint) AS [Source],
                   ch.[Index],
                   ch.[SourceNumber],
                   ch.[Text],
                   VECTOR_DISTANCE('cosine', @queryVector, ch.[Embedding]) AS [Distance]
            FROM wanted AS w
            INNER JOIN [Core].[ConversationDocumentChunk] AS ch
                    ON ch.[ConversationDocumentId] = w.[DocumentId]
                   AND ch.[Index] = w.[Index]
            INNER JOIN [Core].[ConversationDocument] AS cd ON cd.[Id] = ch.[ConversationDocumentId]
            WHERE w.[Source] = 0
              AND cd.[ConversationId] = @conversationId
              AND cd.[DateDeactivated] IS NULL
              AND ch.[DateDeactivated] IS NULL
            UNION ALL
            SELECT ph.[ProjectDocumentId],
                   CAST(1 AS tinyint),
                   ph.[Index],
                   ph.[SourceNumber],
                   ph.[Text],
                   VECTOR_DISTANCE('cosine', @queryVector, ph.[Embedding])
            FROM wanted AS w
            INNER JOIN [Core].[ProjectDocumentChunk] AS ph
                    ON ph.[ProjectDocumentId] = w.[DocumentId]
                   AND ph.[Index] = w.[Index]
            INNER JOIN [Core].[ProjectDocument] AS pd ON pd.[Id] = ph.[ProjectDocumentId]
            WHERE w.[Source] = 1
              AND @projectId IS NOT NULL
              AND pd.[ProjectId] = @projectId
              AND pd.[DateDeactivated] IS NULL
              AND ph.[DateDeactivated] IS NULL;
            """;
    }
}
