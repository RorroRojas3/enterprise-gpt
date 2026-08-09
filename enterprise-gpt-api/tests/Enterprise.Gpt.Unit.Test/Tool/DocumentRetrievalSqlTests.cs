using Enterprise.Gpt.Service.Tool;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tool;

/// <summary>
/// Covers the generated retrieval SQL. The statements cannot be executed here — SQLite has no
/// <c>vector</c> type — so what is asserted is the part that is decided in C#: that only the shape of a
/// statement varies with the query, never its content, and that the identifiers SQL Server treats
/// specially are written the way it needs them.
/// </summary>
public sealed class DocumentRetrievalSqlTests
{
    [Fact]
    public void DenseSearch_ReturnsNoEmbeddingToTheClient()
    {
        var sql = DocumentRetrievalSql.DenseSearch;

        Assert.Contains("VECTOR_DISTANCE('cosine', @queryVector, c.[Embedding])", sql, StringComparison.Ordinal);

        // Returning the column would ship 6 KB per candidate row for a value the caller never reads,
        // which is the whole reason this statement is hand-written rather than a LINQ query. The inner
        // derived table may name it — SQL Server projects only what the outer query asks for — but the
        // outer select list must mention it exactly once, inside VECTOR_DISTANCE.
        var projection = sql[..sql.IndexOf("FROM (", StringComparison.Ordinal)];
        Assert.Equal(1, CountOccurrences(projection, "[Embedding]"));
    }

    [Fact]
    public void BuildFetchChunks_ReturnsNoEmbeddingToTheClient()
    {
        var sql = DocumentRetrievalSql.BuildFetchChunks(2);

        Assert.Equal(2, CountOccurrences(sql, "VECTOR_DISTANCE('cosine', @queryVector,"));
        Assert.Equal(2, CountOccurrences(sql, "[Embedding]"));
    }

    [Fact]
    public void DenseSearch_ScopesToTheConversationAndItsProjectAndFiltersSoftDeletesAtEveryLevel()
    {
        var sql = DocumentRetrievalSql.DenseSearch;

        Assert.Contains("cd.[ConversationId] = @conversationId", sql, StringComparison.Ordinal);
        Assert.Contains("pd.[ProjectId] = @projectId", sql, StringComparison.Ordinal);
        Assert.Contains("@projectId IS NOT NULL", sql, StringComparison.Ordinal);

        // Chunk, document, and a standalone conversation skipping the project half entirely. There is no
        // query filter on this model, so every one of these has to be written by hand.
        Assert.Contains("ch.[DateDeactivated] IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("cd.[DateDeactivated] IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ph.[DateDeactivated] IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("pd.[DateDeactivated] IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStatement_BracketsTheReservedIndexColumn()
    {
        foreach (var sql in AllStatements())
        {
            Assert.Contains("[Index]", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(".Index", sql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void BuildLexicalSearch_EmitsOneParameterisedArmPerTerm(int termCount)
    {
        var sql = DocumentRetrievalSql.BuildLexicalSearch(termCount);

        for (var i = 0; i < termCount; i++)
        {
            Assert.Contains($"LIKE @t{i} ESCAPE", sql, StringComparison.Ordinal);
        }

        Assert.DoesNotContain($"@t{termCount} ", sql, StringComparison.Ordinal);
        Assert.Equal(termCount, CountOccurrences(sql, "CASE WHEN c.[Text]"));
    }

    [Fact]
    public void BuildLexicalSearch_MatchesCaseAndAccentInsensitivelyWhateverTheDatabaseCollation()
    {
        Assert.Contains("COLLATE Latin1_General_CI_AI", DocumentRetrievalSql.BuildLexicalSearch(1), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLexicalSearch_ScoresOnceAndFiltersOnTheScore()
    {
        var sql = DocumentRetrievalSql.BuildLexicalSearch(3);

        // Repeating the comparisons in a WHERE clause would evaluate every LIKE twice per row.
        Assert.Contains("WHERE m.[MatchCount] > 0", sql, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(sql, "LIKE @t"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildLexicalSearch_NoTerms_Throws(int termCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentRetrievalSql.BuildLexicalSearch(termCount));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void BuildFetchChunks_EmitsThreeParametersPerChunkKey(int keyCount)
    {
        var sql = DocumentRetrievalSql.BuildFetchChunks(keyCount);

        for (var i = 0; i < keyCount; i++)
        {
            Assert.Contains($"(@d{i}, @s{i}, @i{i})", sql, StringComparison.Ordinal);
        }

        Assert.DoesNotContain($"@d{keyCount},", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFetchChunks_ReEstablishesOwnershipRatherThanTrustingTheKeys()
    {
        var sql = DocumentRetrievalSql.BuildFetchChunks(2);

        Assert.Contains("cd.[ConversationId] = @conversationId", sql, StringComparison.Ordinal);
        Assert.Contains("pd.[ProjectId] = @projectId", sql, StringComparison.Ordinal);
        Assert.Contains("ch.[DateDeactivated] IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ph.[DateDeactivated] IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFetchChunks_SeeksChunksByExactIndex()
    {
        var sql = DocumentRetrievalSql.BuildFetchChunks(2);

        // The caller has already expanded and de-duplicated the neighbour window, so this must be an
        // equality seek on the unique filtered index rather than a range scan.
        Assert.Contains("ch.[Index] = w.[Index]", sql, StringComparison.Ordinal);
        Assert.Contains("ph.[Index] = w.[Index]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("BETWEEN", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void BuildFetchChunks_NoKeys_Throws(int keyCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentRetrievalSql.BuildFetchChunks(keyCount));
    }

    [Fact]
    public void GeneratedStatements_ContainNoLiteralValues()
    {
        // The single check that matters most for review: the number of comparison arms and value rows
        // varies with the query, but nothing derived from user input is ever concatenated into the text.
        // Every varying fragment is a parameter placeholder.
        var statements = new[]
        {
            DocumentRetrievalSql.BuildLexicalSearch(4),
            DocumentRetrievalSql.BuildFetchChunks(4)
        };

        foreach (var sql in statements)
        {
            var quoted = sql.Split('\'');

            // Odd-numbered segments are the contents of single-quoted literals. Only the fixed strings
            // this module is allowed to write should appear there.
            for (var i = 1; i < quoted.Length; i += 2)
            {
                Assert.Contains(quoted[i], new[] { "cosine", "\\" }, StringComparer.Ordinal);
            }
        }
    }

    private static IEnumerable<string> AllStatements() =>
    [
        DocumentRetrievalSql.DenseSearch,
        DocumentRetrievalSql.BuildLexicalSearch(2),
        DocumentRetrievalSql.BuildFetchChunks(2)
    ];

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
