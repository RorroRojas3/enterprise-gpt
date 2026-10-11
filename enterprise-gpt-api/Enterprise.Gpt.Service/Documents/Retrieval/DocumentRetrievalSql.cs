using System.Text;

namespace Enterprise.Gpt.Service.Documents.Retrieval;

/// <summary>
/// The hand-written T-SQL behind document retrieval.
/// </summary>
/// <remarks>
/// Hand-written because EF would materialize the 6 KB embedding per row, and with no soft-delete query filter
/// here every level is filtered by hand. No vector index, deliberately: candidates are already narrowed to one
/// conversation and project, and a preview Azure SQL vector index makes its table read-only (or, under
/// <c>ALLOW_STALE_VECTOR_INDEX</c>, stale), which breaks upload; revisit at general availability.
/// </remarks>
internal static class DocumentRetrievalSql
{
    internal const string TermParameterPrefix = "@t";

    internal const string DocumentParameterPrefix = "@d";
    internal const string SourceParameterPrefix = "@s";
    internal const string IndexParameterPrefix = "@i";

    // Derived table aliased c. The project half is gated on @projectId IS NOT NULL so a standalone
    // conversation skips it; Text and Embedding are read only when the outer statement projects them.
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
    /// Cosine <c>VECTOR_DISTANCE</c> is a distance in [0, 2] (smaller is closer); row order is the pass rank.
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

    #region Public static methods

    /// <summary>
    /// Builds the keyword pass: the scoped chunks scored by how many of the query terms they contain,
    /// best first.
    /// </summary>
    /// <param name="termCount">The number of terms, each of which becomes one <c>@tN</c> parameter.</param>
    /// <remarks>
    /// Only the arm count varies; term values are always bound parameters (LIKE-escaped by the caller). LIKE
    /// because full-text cannot be assumed installed, under an explicit CI_AI collation; ties favour the
    /// shorter, denser chunk.
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
    /// <remarks>
    /// Exact seeks on each table's unique filtered <c>(DocumentId, Index)</c> index. Ownership predicates are
    /// repeated rather than trusted from the earlier pass, since the keys arrive as parameters.
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

    #endregion
}
