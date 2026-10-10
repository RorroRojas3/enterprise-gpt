using Enterprise.Gpt.Common.Enums;
using System.Text;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// The shape of one filter, with the value it compares against deliberately absent.
/// </summary>
/// <remarks>
/// Only a column's already-resolved type and a comparator drawn from a closed set reach statement
/// construction; the value itself never does, which is what makes every generated command text a
/// function of shape alone.
/// </remarks>
internal readonly record struct SheetFilterSpec(SheetColumnType ColumnType, SheetFilterComparator Comparator);

/// <summary>
/// The hand-written T-SQL behind the deterministic spreadsheet lookup.
/// </summary>
/// <remarks>
/// Nothing the model supplies is ever concatenated into a command text: a column reaches a statement
/// only as its already-resolved <see cref="SheetColumnType"/> choosing between pre-authored fragments,
/// its name travels as a bound JSON-path parameter, and only the <em>number</em> of filter arms varies
/// with the request. Soft delete has no query filter in this model, so the row, its sheet and the
/// document that owns it are each filtered by hand in both arms. See <c>docs/documents/sheet-query.md</c>
/// for why values are read with <c>TRY_CONVERT</c> rather than <c>JSON_VALUE … RETURNING</c>.
/// </remarks>
internal static class SheetQuerySql
{
    /// <summary>
    /// Parameter carrying the JSON path of the column an aggregate is computed over.
    /// </summary>
    internal const string ValuePathParameter = "@valuePath";

    /// <summary>
    /// Parameter carrying the JSON path of the grouping column.
    /// </summary>
    internal const string GroupPathParameter = "@groupPath";

    /// <summary>
    /// Prefixes for the generated per-filter path and value parameters.
    /// </summary>
    internal const string FilterPathPrefix = "@fp";
    internal const string FilterValuePrefix = "@fv";

    /// <summary>
    /// The queryable rows of one sheet, as a derived table aliased <c>r</c>.
    /// </summary>
    /// <remarks>
    /// Ownership is re-established here from the conversation and project rather than trusted from the
    /// pass that resolved <c>@sheetId</c>, because this statement takes that id as a parameter. The
    /// project half is gated on <c>@projectId IS NOT NULL</c> first, so a standalone conversation skips
    /// it entirely rather than joining on a null.
    /// </remarks>
    private const string ScopedSheetRows = """
        (
            SELECT r.[RowIndex], r.[Cells]
            FROM [Core].[ConversationDocumentSheetRow] AS r
            INNER JOIN [Core].[ConversationDocumentSheet] AS s ON s.[Id] = r.[ConversationDocumentSheetId]
            INNER JOIN [Core].[ConversationDocument] AS cd ON cd.[Id] = s.[ConversationDocumentId]
            WHERE s.[Id] = @sheetId
              AND cd.[ConversationId] = @conversationId
              AND cd.[DateDeactivated] IS NULL
              AND s.[DateDeactivated] IS NULL
              AND r.[DateDeactivated] IS NULL
            UNION ALL
            SELECT pr.[RowIndex], pr.[Cells]
            FROM [Core].[ProjectDocumentSheetRow] AS pr
            INNER JOIN [Core].[ProjectDocumentSheet] AS ps ON ps.[Id] = pr.[ProjectDocumentSheetId]
            INNER JOIN [Core].[ProjectDocument] AS pd ON pd.[Id] = ps.[ProjectDocumentId]
            WHERE @projectId IS NOT NULL
              AND ps.[Id] = @sheetId
              AND pd.[ProjectId] = @projectId
              AND pd.[DateDeactivated] IS NULL
              AND ps.[DateDeactivated] IS NULL
              AND pr.[DateDeactivated] IS NULL
        ) AS r
        """;

    /// <summary>
    /// Builds an ungrouped aggregate over one sheet.
    /// </summary>
    /// <param name="operation">The aggregate to compute.</param>
    /// <param name="valueType">The aggregated column's inferred type, or <see langword="null"/> for a bare count.</param>
    /// <param name="filters">The shape of each filter, in order.</param>
    /// <returns>The command text.</returns>
    /// <remarks>
    /// Returns one row of three columns. <c>MatchedRows</c> and <c>ValueRows</c> are both reported so a
    /// total that skipped cells its column's type could not read is never mistaken for a complete one.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="filters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not an aggregate.</exception>
    internal static string BuildAggregate(
        SheetQueryOperation operation, SheetColumnType? valueType, IReadOnlyList<SheetFilterSpec> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        return $"""
            SELECT COUNT(*) AS [MatchedRows],
                   {ValueRowsExpression(operation, valueType)} AS [ValueRows],
                   {AggregateExpression(operation, valueType)} AS [Value]
            FROM {ScopedSheetRows}
            {BuildWhere(filters)};
            """;
    }

    /// <summary>
    /// Builds an aggregate grouped by a second column.
    /// </summary>
    /// <param name="operation">The aggregate to compute.</param>
    /// <param name="valueType">The aggregated column's inferred type, or <see langword="null"/> for a bare count.</param>
    /// <param name="filters">The shape of each filter, in order.</param>
    /// <returns>The command text.</returns>
    /// <remarks>
    /// Ordered by the group key rather than by the aggregate so the same question returns the same rows
    /// in the same order, and so a result trimmed by the group cap is trimmed predictably.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="filters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not an aggregate.</exception>
    internal static string BuildGroupedAggregate(
        SheetQueryOperation operation, SheetColumnType? valueType, IReadOnlyList<SheetFilterSpec> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        return $"""
            SELECT TOP (@maxGroups)
                   {RawExpression(GroupPathParameter)} COLLATE Latin1_General_CI_AI AS [GroupKey],
                   COUNT(*) AS [MatchedRows],
                   {ValueRowsExpression(operation, valueType)} AS [ValueRows],
                   {AggregateExpression(operation, valueType)} AS [Value]
            FROM {ScopedSheetRows}
            {BuildWhere(filters)}
            GROUP BY {RawExpression(GroupPathParameter)} COLLATE Latin1_General_CI_AI
            ORDER BY [GroupKey];
            """;
    }

    /// <summary>
    /// Builds the matching-row listing.
    /// </summary>
    /// <param name="filters">The shape of each filter, in order.</param>
    /// <returns>The command text.</returns>
    /// <remarks>
    /// <c>COUNT(*) OVER ()</c> carries the full match count on every returned row, so the cap can be
    /// reported as a truncation instead of costing a second round trip to discover.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="filters"/> is <see langword="null"/>.</exception>
    internal static string BuildRowList(IReadOnlyList<SheetFilterSpec> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        return $"""
            SELECT TOP (@maxRows)
                   r.[RowIndex],
                   r.[Cells],
                   COUNT(*) OVER () AS [MatchedRows]
            FROM {ScopedSheetRows}
            {BuildWhere(filters)}
            ORDER BY r.[RowIndex];
            """;
    }

    /// <summary>
    /// The typed read of one cell, chosen by the column's own inferred type.
    /// </summary>
    private static string ValueExpression(SheetColumnType type, string pathParameter) => type switch
    {
        // Inference accepts a group separator, so a comma-formatted export infers as a number while
        // CONVERT alone would yield NULL for it. A decimal-comma locale fails that same inference and
        // lands as text, so stripping them here cannot corrupt one.
        SheetColumnType.Number => $"TRY_CONVERT(float, REPLACE(JSON_VALUE(r.[Cells], {pathParameter}), ',', ''))",
        SheetColumnType.Date => $"TRY_CONVERT(datetime2, JSON_VALUE(r.[Cells], {pathParameter}))",

        // Boolean has no RETURNING type and no useful ordering, so it compares as the text it is stored as.
        _ => $"JSON_VALUE(r.[Cells], {pathParameter}) COLLATE Latin1_General_CI_AI"
    };

    /// <summary>
    /// The raw text of one cell, for the comparisons and counts that must not coerce it.
    /// </summary>
    private static string RawExpression(string pathParameter) => $"JSON_VALUE(r.[Cells], {pathParameter})";

    /// <summary>
    /// Counts rows that hold a value in the aggregated column at all, rather than rows whose value the
    /// column's type could read — otherwise "how many rows have a status" would silently mean "how many
    /// of them parse as one".
    /// </summary>
    private static string CountExpression(SheetColumnType? valueType) =>
        valueType is null ? "COUNT(*)" : $"COUNT({RawExpression(ValuePathParameter)})";

    /// <summary>
    /// Counts the rows that actually contributed to the aggregate, so a total computed over fewer rows
    /// than matched is reportable rather than silent.
    /// </summary>
    private static string ValueRowsExpression(SheetQueryOperation operation, SheetColumnType? valueType) =>
        operation is SheetQueryOperation.Count || valueType is null
            ? CountExpression(valueType)
            : $"COUNT({ValueExpression(valueType.Value, ValuePathParameter)})";

    private static string AggregateExpression(SheetQueryOperation operation, SheetColumnType? valueType)
    {
        if (operation is SheetQueryOperation.Count)
        {
            return CountExpression(valueType);
        }

        var function = operation switch
        {
            SheetQueryOperation.Sum => "SUM",
            SheetQueryOperation.Avg => "AVG",
            SheetQueryOperation.Min => "MIN",
            SheetQueryOperation.Max => "MAX",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        if (valueType is not { } type)
        {
            throw new ArgumentOutOfRangeException(nameof(valueType), valueType, null);
        }

        return $"{function}({ValueExpression(type, ValuePathParameter)})";
    }

    private static string BuildWhere(IReadOnlyList<SheetFilterSpec> filters)
    {
        if (filters.Count == 0)
        {
            return string.Empty;
        }

        var arms = new StringBuilder();

        for (var i = 0; i < filters.Count; i++)
        {
            arms.Append(i == 0 ? "WHERE " : "  AND ");
            arms.AppendLine(BuildArm(filters[i], i));
        }

        return arms.ToString().TrimEnd();
    }

    private static string BuildArm(SheetFilterSpec filter, int index)
    {
        var path = $"{FilterPathPrefix}{index}";
        var value = $"{FilterValuePrefix}{index}";

        if (filter.Comparator is SheetFilterComparator.Contains)
        {
            // Matched against the cell's own text whatever the column's type, so "contains 2026" works on
            // a date column as readily as on a free-text one. The caller escapes the pattern's wildcards.
            return $"{RawExpression(path)} COLLATE Latin1_General_CI_AI LIKE {value} ESCAPE '\\'";
        }

        var expression = ValueExpression(filter.ColumnType, path);

        // A row with no value in the column genuinely is not equal to the one named, and a bare <> would
        // drop it on the NULL rather than keep it.
        if (filter.Comparator is SheetFilterComparator.Neq)
        {
            return $"({expression} IS NULL OR {expression} <> {value})";
        }

        var comparison = filter.Comparator switch
        {
            SheetFilterComparator.Eq => "=",
            SheetFilterComparator.Gt => ">",
            SheetFilterComparator.Gte => ">=",
            SheetFilterComparator.Lt => "<",
            SheetFilterComparator.Lte => "<=",
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Comparator, null)
        };

        return $"{expression} {comparison} {value}";
    }
}
