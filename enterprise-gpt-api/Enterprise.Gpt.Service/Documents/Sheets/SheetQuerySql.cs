using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Documents.Sheets.Models;

namespace Enterprise.Gpt.Service.Documents.Sheets;

/// <summary>
/// The hand-written T-SQL behind the deterministic spreadsheet lookup.
/// </summary>
/// <remarks>
/// No model text is concatenated: a column only picks a pre-authored fragment by its
/// <see cref="SheetColumnTypes"/> and its name binds as a JSON-path parameter. Soft delete is filtered by
/// hand at every level; see <c>docs/documents/sheet-query.md</c> for why values use <c>TRY_CONVERT</c>.
/// </remarks>
internal static class SheetQuerySql
{
    internal const string ValuePathParameter = "@valuePath";

    internal const string GroupPathParameter = "@groupPath";

    internal const string FilterPathPrefix = "@fp";
    internal const string FilterValuePrefix = "@fv";

    /// <summary>
    /// One sheet's queryable rows as derived table <c>r</c>. Ownership is re-checked here rather than
    /// trusted from the pass that resolved <c>@sheetId</c>, since the id arrives as a parameter.
    /// </summary>
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

    #region Private methods

    private static string ValueExpression(SheetColumnTypes type, string pathParameter) => type switch
    {
        // Inference accepts group separators, so they are stripped; decimal-comma values infer as text and
        // never reach this arm.
        SheetColumnTypes.Number => $"TRY_CONVERT(float, REPLACE(JSON_VALUE(r.[Cells], {pathParameter}), ',', ''))",
        SheetColumnTypes.Date => $"TRY_CONVERT(datetime2, JSON_VALUE(r.[Cells], {pathParameter}))",

        // Boolean has no RETURNING type and no useful ordering, so it compares as the text it is stored as.
        _ => $"JSON_VALUE(r.[Cells], {pathParameter}) COLLATE Latin1_General_CI_AI"
    };

    private static string RawExpression(string pathParameter) => $"JSON_VALUE(r.[Cells], {pathParameter})";

    /// <summary>
    /// Counts rows with any value in the column, not rows its type can read, so "how many have a status"
    /// does not silently mean "how many parse".
    /// </summary>
    private static string CountExpression(SheetColumnTypes? valueType) =>
        valueType is null ? "COUNT(*)" : $"COUNT({RawExpression(ValuePathParameter)})";

    /// <summary>
    /// Counts the rows that fed the aggregate, so a total over fewer rows than matched is reportable.
    /// </summary>
    private static string ValueRowsExpression(SheetQueryOperations operation, SheetColumnTypes? valueType) =>
        operation is SheetQueryOperations.Count || valueType is null
            ? CountExpression(valueType)
            : $"COUNT({ValueExpression(valueType.Value, ValuePathParameter)})";

    private static string AggregateExpression(SheetQueryOperations operation, SheetColumnTypes? valueType)
    {
        if (operation is SheetQueryOperations.Count)
        {
            return CountExpression(valueType);
        }

        var function = operation switch
        {
            SheetQueryOperations.Sum => "SUM",
            SheetQueryOperations.Avg => "AVG",
            SheetQueryOperations.Min => "MIN",
            SheetQueryOperations.Max => "MAX",
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

        if (filter.Comparator is SheetFilterComparators.Contains)
        {
            // Raw text whatever the type, so "contains 2026" works on dates; the caller escapes wildcards.
            return $"{RawExpression(path)} COLLATE Latin1_General_CI_AI LIKE {value} ESCAPE '\\'";
        }

        var expression = ValueExpression(filter.ColumnType, path);

        // An empty cell is not equal to the value, but a bare <> would drop it on the NULL.
        if (filter.Comparator is SheetFilterComparators.Neq)
        {
            return $"({expression} IS NULL OR {expression} <> {value})";
        }

        var comparison = filter.Comparator switch
        {
            SheetFilterComparators.Eq => "=",
            SheetFilterComparators.Gt => ">",
            SheetFilterComparators.Gte => ">=",
            SheetFilterComparators.Lt => "<",
            SheetFilterComparators.Lte => "<=",
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Comparator, null)
        };

        return $"{expression} {comparison} {value}";
    }

    #endregion

    #region Public static methods

    /// <summary>
    /// Builds an ungrouped aggregate; a <see langword="null"/> value type means a bare count.
    /// </summary>
    /// <remarks>
    /// Reports <c>MatchedRows</c> and <c>ValueRows</c> so a total that skipped unreadable cells is never
    /// mistaken for a complete one.
    /// </remarks>
    internal static string BuildAggregate(
        SheetQueryOperations operation, SheetColumnTypes? valueType, IReadOnlyList<SheetFilterSpec> filters)
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
    /// Builds an aggregate grouped by a second column, ordered by group key so the result and the group
    /// cap's trimming are deterministic.
    /// </summary>
    internal static string BuildGroupedAggregate(
        SheetQueryOperations operation, SheetColumnTypes? valueType, IReadOnlyList<SheetFilterSpec> filters)
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
    /// Builds the matching-row listing; <c>COUNT(*) OVER ()</c> carries the full match count, so truncation
    /// is reported without a second round trip.
    /// </summary>
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

    #endregion
}
