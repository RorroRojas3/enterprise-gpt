using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Tool;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tool;

/// <summary>
/// Covers the generated sheet-query SQL. The statements cannot be executed here — SQLite derives its own
/// store type for the <c>json</c> cells column and has no <c>JSON_VALUE</c> of SQL Server's shape — so
/// what is asserted is the part decided in C#: that only the shape of a statement varies with the
/// request, never its content, and that no column name ever reaches the command text.
/// </summary>
public sealed class SheetQuerySqlTests
{
    [Fact]
    public void GeneratedStatements_ContainNoLiteralValues()
    {
        // The single check that matters most for review: the number of filter arms varies with the
        // request, but nothing derived from the model's arguments is ever concatenated into the text.
        // Every varying fragment is a parameter placeholder.
        foreach (var sql in AllStatements())
        {
            var quoted = sql.Split('\'');

            // Odd-numbered segments are the contents of single-quoted literals. Only the fixed strings
            // this module is allowed to write should appear there: the LIKE escape character, and the
            // group separator a numeric read strips along with the empty string it is replaced by.
            for (var i = 1; i < quoted.Length; i += 2)
            {
                Assert.Contains(quoted[i], new[] { "\\", ",", "" }, StringComparer.Ordinal);
            }
        }
    }

    [Fact]
    public void GeneratedStatements_NeverCarryAJsonPath()
    {
        // A path built by concatenation would show up here immediately, and a path is the only place a
        // model-named column could reach a statement at all.
        foreach (var sql in AllStatements())
        {
            Assert.DoesNotContain("$.", sql, StringComparison.Ordinal);
            Assert.Contains("JSON_VALUE(r.[Cells], @", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GeneratedStatements_ScopeToTheSheetAndItsOwnerAndFilterSoftDeletesAtEveryLevel()
    {
        foreach (var sql in AllStatements())
        {
            Assert.Contains("s.[Id] = @sheetId", sql, StringComparison.Ordinal);
            Assert.Contains("ps.[Id] = @sheetId", sql, StringComparison.Ordinal);

            // Ownership is re-established here rather than trusted from the pass that resolved the sheet
            // id, because this statement takes that id as a parameter.
            Assert.Contains("cd.[ConversationId] = @conversationId", sql, StringComparison.Ordinal);
            Assert.Contains("pd.[ProjectId] = @projectId", sql, StringComparison.Ordinal);
            Assert.Contains("@projectId IS NOT NULL", sql, StringComparison.Ordinal);

            // Row, sheet and document, on both arms. There is no query filter on this model, so every
            // one of these has to be written by hand.
            foreach (var alias in new[] { "r", "s", "cd", "pr", "ps", "pd" })
            {
                Assert.Contains($"{alias}.[DateDeactivated] IS NULL", sql, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void GeneratedStatements_CapRowsAndGroupsWithAParameter()
    {
        Assert.Contains("TOP (@maxRows)", SheetQuerySql.BuildRowList([]), StringComparison.Ordinal);
        Assert.Contains(
            "TOP (@maxGroups)",
            SheetQuerySql.BuildGroupedAggregate(SheetQueryOperation.Sum, SheetColumnType.Number, []),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SheetColumnType.Number, "TRY_CONVERT(float,")]
    [InlineData(SheetColumnType.Date, "TRY_CONVERT(datetime2,")]
    public void BuildAggregate_ReadsTheColumnAsItsOwnInferredType(SheetColumnType type, string expected)
    {
        var sql = SheetQuerySql.BuildAggregate(SheetQueryOperation.Min, type, []);

        Assert.Contains(expected, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAggregate_BooleanColumn_ComparesAsTextRatherThanReturningBit()
    {
        var sql = SheetQuerySql.BuildAggregate(SheetQueryOperation.Max, SheetColumnType.Boolean, []);

        Assert.DoesNotContain("RETURNING", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("bit", sql, StringComparison.Ordinal);
        Assert.Contains("COLLATE Latin1_General_CI_AI", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAggregate_CountWithNoColumn_CountsRowsRatherThanValues()
    {
        var sql = SheetQuerySql.BuildAggregate(SheetQueryOperation.Count, null, []);

        Assert.DoesNotContain("JSON_VALUE(r.[Cells], @valuePath)", sql, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(sql, "COUNT(*)"));
    }

    [Fact]
    public void BuildAggregate_CountWithAColumn_CountsPopulatedCellsRatherThanReadableOnes()
    {
        var sql = SheetQuerySql.BuildAggregate(SheetQueryOperation.Count, SheetColumnType.Number, []);

        // Counting the coerced value would answer "how many of them parse as a number" to a question
        // that asked how many rows have one at all.
        Assert.DoesNotContain("COUNT(TRY_CONVERT", sql, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(sql, "COUNT(JSON_VALUE(r.[Cells], @valuePath))"));
    }

    [Fact]
    public void BuildAggregate_ReportsBothTheMatchedRowsAndTheRowsThatContributed()
    {
        var sql = SheetQuerySql.BuildAggregate(SheetQueryOperation.Sum, SheetColumnType.Number, []);

        Assert.Contains("COUNT(*) AS [MatchedRows]", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(TRY_CONVERT(float,", sql, StringComparison.Ordinal);
        Assert.Contains("SUM(TRY_CONVERT(float,", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGroupedAggregate_GroupsAndOrdersOnTheGroupKeyRatherThanTheAggregate()
    {
        var sql = SheetQuerySql.BuildGroupedAggregate(SheetQueryOperation.Sum, SheetColumnType.Number, []);

        Assert.Contains("GROUP BY JSON_VALUE(r.[Cells], @groupPath)", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY [GroupKey]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRowList_CarriesTheFullMatchCountOnEveryRow()
    {
        var sql = SheetQuerySql.BuildRowList([]);

        // Without the window function the cap could not be told apart from an exhausted result without a
        // second round trip.
        Assert.Contains("COUNT(*) OVER () AS [MatchedRows]", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.[RowIndex]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRowList_NoFilters_EmitsNoWhereClauseOfItsOwn()
    {
        var sql = SheetQuerySql.BuildRowList([]);

        // The derived table's own predicates are the only WHERE the statement should carry.
        Assert.Equal(2, CountOccurrences(sql, "WHERE "));
    }

    [Fact]
    public void BuildRowList_OneArmPerFilterAndNoneBeyondThem()
    {
        const int filterCount = 4;

        var sql = SheetQuerySql.BuildRowList([.. Enumerable
            .Range(0, filterCount)
            .Select(_ => new SheetFilterSpec(SheetColumnType.Text, SheetFilterComparator.Eq))]);

        for (var i = 0; i < filterCount; i++)
        {
            Assert.Contains($"@fp{i}", sql, StringComparison.Ordinal);
            Assert.Contains($"@fv{i}", sql, StringComparison.Ordinal);
        }

        Assert.DoesNotContain($"@fp{filterCount}", sql, StringComparison.Ordinal);
        Assert.DoesNotContain($"@fv{filterCount}", sql, StringComparison.Ordinal);
        Assert.Equal(filterCount, CountOccurrences(sql, "@fv"));
    }

    [Fact]
    public void BuildRowList_NotEqual_KeepsRowsWithNoValueInTheColumn()
    {
        var sql = SheetQuerySql.BuildRowList([new SheetFilterSpec(SheetColumnType.Text, SheetFilterComparator.Neq)]);

        // A bare <> is NULL for a row whose cell is absent, which would drop rows that genuinely are not
        // equal to the value named.
        Assert.Contains("IS NULL OR", sql, StringComparison.Ordinal);
        Assert.Contains("<> @fv0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRowList_Contains_MatchesTheCellsOwnTextWithWildcardsEscaped()
    {
        var sql = SheetQuerySql.BuildRowList(
            [new SheetFilterSpec(SheetColumnType.Number, SheetFilterComparator.Contains)]);

        // Matched as text whatever the column holds, so a contains filter never coerces.
        Assert.DoesNotContain("TRY_CONVERT", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE @fv0 ESCAPE '\\'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedStatements_NeverIndexOrOrderOnTheCellsColumn()
    {
        foreach (var sql in AllStatements())
        {
            Assert.DoesNotContain("GROUP BY r.[Cells]", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("ORDER BY r.[Cells]", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Builders_NullFilters_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => SheetQuerySql.BuildAggregate(SheetQueryOperation.Sum, SheetColumnType.Number, null!));
        Assert.Throws<ArgumentNullException>(
            () => SheetQuerySql.BuildGroupedAggregate(SheetQueryOperation.Sum, SheetColumnType.Number, null!));
        Assert.Throws<ArgumentNullException>(() => SheetQuerySql.BuildRowList(null!));
    }

    [Fact]
    public void BuildAggregate_AnAggregateWithNoColumnType_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SheetQuerySql.BuildAggregate(SheetQueryOperation.Sum, null, []));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SheetQuerySql.BuildAggregate(SheetQueryOperation.Filter, SheetColumnType.Number, []));
    }

    private static IEnumerable<string> AllStatements()
    {
        SheetFilterSpec[] filters =
        [
            new(SheetColumnType.Number, SheetFilterComparator.Gte),
            new(SheetColumnType.Text, SheetFilterComparator.Contains),
            new(SheetColumnType.Date, SheetFilterComparator.Lt),
            new(SheetColumnType.Boolean, SheetFilterComparator.Neq)
        ];

        yield return SheetQuerySql.BuildAggregate(SheetQueryOperation.Sum, SheetColumnType.Number, filters);
        yield return SheetQuerySql.BuildAggregate(SheetQueryOperation.Count, null, filters);
        yield return SheetQuerySql.BuildGroupedAggregate(SheetQueryOperation.Avg, SheetColumnType.Number, filters);
        yield return SheetQuerySql.BuildRowList(filters);
    }

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
