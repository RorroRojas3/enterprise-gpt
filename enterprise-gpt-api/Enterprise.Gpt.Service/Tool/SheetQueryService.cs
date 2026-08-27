using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// Computes exact answers over the cells of an ingested spreadsheet.
/// </summary>
public interface ISheetQueryService
{
    /// <summary>
    /// Aggregates or filters one sheet's rows, refusing by name anything it cannot resolve.
    /// </summary>
    /// <param name="scope">The corpus this turn may query, from <see cref="IDocumentRetrievalService.GetScopeAsync"/>.</param>
    /// <param name="operation">The operation the model asked for, unvalidated.</param>
    /// <param name="sheetName">The sheet the model named, or <see langword="null"/> to use the only one in scope.</param>
    /// <param name="column">The column to aggregate, or <see langword="null"/>.</param>
    /// <param name="groupBy">The column to group the aggregate by, or <see langword="null"/>.</param>
    /// <param name="filters">The row filters, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The result, which carries a refusal rather than throwing when a name does not resolve.</returns>
    Task<SheetQueryResult> QueryAsync(
        DocumentRetrievalScope scope,
        string? operation,
        string? sheetName,
        string? column,
        string? groupBy,
        IReadOnlyList<SheetFilter>? filters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reports whether any document in the scope has a sheet stored to query.
    /// </summary>
    /// <param name="scope">The corpus this turn may query.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns><see langword="true"/> when at least one sheet is reachable.</returns>
    /// <remarks>
    /// A spreadsheet extension is not the same fact: a file uploaded before the sheet tables existed has
    /// chunks and no rows, and attaching over it would advertise a workbook every call then denies.
    /// </remarks>
    Task<bool> HasQueryableSheetsAsync(DocumentRetrievalScope scope, CancellationToken cancellationToken);
}

/// <summary>
/// Answers a computed question about a spreadsheet from the rows ingestion stored, with no vector
/// search and no model call of its own.
/// </summary>
/// <remarks>
/// Every name the model supplies is resolved against the caller's own sheet and column rows before any
/// statement is built, and an unresolved name refuses by name rather than falling through to a query
/// built on a guess. The turn's scope is the only source of the documents in play, so a sheet the caller
/// cannot already read through <c>document_search</c> cannot be reached here either.
/// </remarks>
public sealed class SheetQueryService(
    ILogger<SheetQueryService> logger,
    IOptions<SheetQueryOptions> options,
    EnterpriseGptDbContext ctx) : ISheetQueryService
{
    /// <summary>
    /// Bound length of every text parameter, matching the widest value <c>JSON_VALUE</c> returns without
    /// an explicit return type.
    /// </summary>
    private const int TextParameterLength = 4000;

    /// <summary>
    /// Largest number of columns a refusal names before it says how many it left out.
    /// </summary>
    /// <remarks>
    /// A refusal is the one reply no cap in <see cref="SheetQueryOptions"/> bounds, and a sheet may hold
    /// two hundred columns whose names then all land in the turn's context.
    /// </remarks>
    private const int MaxDescribedColumns = 24;

    /// <summary>
    /// Largest number of sheets a refusal describes before it says how many it left out.
    /// </summary>
    private const int MaxDescribedSheets = 20;

    /// <summary>
    /// The date SQL Server anchors a time-only value to when it converts one to <c>datetime2</c>.
    /// </summary>
    private static readonly DateTime TimeOnlyEpoch = new(1900, 1, 1);

    private readonly ILogger<SheetQueryService> _logger = logger;
    private readonly SheetQueryOptions _options = options.Value;
    private readonly EnterpriseGptDbContext _ctx = ctx;

    /// <inheritdoc />
    public async Task<SheetQueryResult> QueryAsync(
        DocumentRetrievalScope scope,
        string? operation,
        string? sheetName,
        string? column,
        string? groupBy,
        IReadOnlyList<SheetFilter>? filters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var echoed = operation?.Trim() ?? string.Empty;

        if (!TryParseOperation(echoed, out var parsed))
        {
            return Refusal(
                echoed,
                $"'{echoed}' is not an operation. Ask again with one of: sum, avg, min, max, count, filter.");
        }

        var sheets = await ResolveSheetsAsync(scope, cancellationToken).ConfigureAwait(false);

        if (sheets.Count == 0)
        {
            return Refusal(echoed, "No spreadsheet is attached to this conversation, so there is nothing to query.");
        }

        if (!TryResolveSheet(sheets, sheetName, echoed, out var sheet, out var sheetRefusal))
        {
            return sheetRefusal;
        }

        if (!TryResolveQuery(parsed, sheet, column, groupBy, filters, echoed, out var query, out var queryRefusal))
        {
            return queryRefusal;
        }

        return await ExecuteAsync(scope, echoed, query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasQueryableSheetsAsync(
        DocumentRetrievalScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var (conversationDocumentIds, projectDocumentIds) = SpreadsheetIds(scope);

        if (conversationDocumentIds.Length > 0
            && await _ctx.ConversationDocumentSheets
                .AnyAsync(
                    s => conversationDocumentIds.Contains(s.ConversationDocumentId) && !s.DateDeactivated.HasValue,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return true;
        }

        return projectDocumentIds.Length > 0
            && await _ctx.ProjectDocumentSheets
                .AnyAsync(
                    s => projectDocumentIds.Contains(s.ProjectDocumentId) && !s.DateDeactivated.HasValue,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the sheets in scope whose name matches <paramref name="wanted"/>, widening the comparison
    /// only as far as it has to.
    /// </summary>
    /// <param name="sheets">The sheets in the turn's scope.</param>
    /// <param name="wanted">The name the model supplied.</param>
    /// <returns>Every match at the narrowest rung that produced one.</returns>
    /// <remarks>
    /// A bare sheet name and its document-qualified label both match, because two workbooks in one
    /// conversation can each hold a sheet called <c>Sheet1</c> and only the label tells them apart.
    /// </remarks>
    internal static IReadOnlyList<ResolvedSheet> MatchSheetByName(IReadOnlyList<ResolvedSheet> sheets, string wanted)
    {
        ArgumentNullException.ThrowIfNull(sheets);

        List<ResolvedSheet> matches =
        [
            .. sheets.Where(s =>
                string.Equals(s.SheetName, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Label, wanted, StringComparison.OrdinalIgnoreCase))
        ];

        if (matches.Count == 0)
        {
            matches =
            [
                .. sheets.Where(s =>
                    s.SheetName.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
                    || s.Label.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            ];
        }

        if (matches.Count == 0)
        {
            matches =
            [
                .. sheets.Where(s =>
                    s.SheetName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                    || s.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            ];
        }

        return matches;
    }

    /// <summary>
    /// Renders a column name as a JSON path, or reports that it cannot be rendered as one.
    /// </summary>
    /// <remarks>
    /// A quoted member name follows JSON string rules, and SQL Server does not document escaping inside
    /// one — so a name carrying a quote, a backslash or a control character is refused by name rather
    /// than turned into a path that might parse as something else.
    /// </remarks>
    internal static bool TryBuildJsonPath(string columnName, out string path)
    {
        foreach (var character in columnName)
        {
            if (character is '"' or '\\' || char.IsControl(character))
            {
                path = string.Empty;

                return false;
            }
        }

        path = string.Create(CultureInfo.InvariantCulture, $"$.\"{columnName}\"");

        return true;
    }

    #region Resolution

    /// <summary>
    /// The scope's spreadsheet documents, split by the table their sheets would live in.
    /// </summary>
    private static (Guid[] Conversation, Guid[] Project) SpreadsheetIds(DocumentRetrievalScope scope)
    {
        var spreadsheets = scope.Documents
            .Where(d => DocumentRetrievalService.SpreadsheetExtensions.Contains(Path.GetExtension(d.Name)))
            .ToArray();

        return (
            [.. spreadsheets.Where(d => d.Source is DocumentSource.Conversation).Select(d => d.Id)],
            [.. spreadsheets.Where(d => d.Source is DocumentSource.Project).Select(d => d.Id)]);
    }

    private async Task<IReadOnlyList<ResolvedSheet>> ResolveSheetsAsync(
        DocumentRetrievalScope scope, CancellationToken cancellationToken)
    {
        var spreadsheets = scope.Documents
            .Where(d => DocumentRetrievalService.SpreadsheetExtensions.Contains(Path.GetExtension(d.Name)))
            .ToArray();

        if (spreadsheets.Length == 0)
        {
            return [];
        }

        var (conversationDocumentIds, projectDocumentIds) = SpreadsheetIds(scope);

        List<SheetRecord> records = [];

        if (conversationDocumentIds.Length > 0)
        {
            var sheets = await _ctx.ConversationDocumentSheets
                .AsNoTracking()
                .Where(s => conversationDocumentIds.Contains(s.ConversationDocumentId) && !s.DateDeactivated.HasValue)
                .Select(s => new
                {
                    s.Id,
                    DocumentId = s.ConversationDocumentId,
                    s.SheetIndex,
                    s.SheetName,
                    s.RowCount,
                    Columns = s.Columns
                        .Where(c => !c.DateDeactivated.HasValue)
                        .OrderBy(c => c.ColumnIndex)
                        .Select(c => new { c.ColumnName, c.InferredType })
                        .ToList()
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            records.AddRange(sheets.Select(s => new SheetRecord(
                s.Id,
                s.DocumentId,
                DocumentSource.Conversation,
                s.SheetIndex,
                s.SheetName,
                s.RowCount,
                [.. s.Columns.Select(c => new ResolvedSheetColumn(c.ColumnName, c.InferredType))])));
        }

        if (projectDocumentIds.Length > 0)
        {
            var sheets = await _ctx.ProjectDocumentSheets
                .AsNoTracking()
                .Where(s => projectDocumentIds.Contains(s.ProjectDocumentId) && !s.DateDeactivated.HasValue)
                .Select(s => new
                {
                    s.Id,
                    DocumentId = s.ProjectDocumentId,
                    s.SheetIndex,
                    s.SheetName,
                    s.RowCount,
                    Columns = s.Columns
                        .Where(c => !c.DateDeactivated.HasValue)
                        .OrderBy(c => c.ColumnIndex)
                        .Select(c => new { c.ColumnName, c.InferredType })
                        .ToList()
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            records.AddRange(sheets.Select(s => new SheetRecord(
                s.Id,
                s.DocumentId,
                DocumentSource.Project,
                s.SheetIndex,
                s.SheetName,
                s.RowCount,
                [.. s.Columns.Select(c => new ResolvedSheetColumn(c.ColumnName, c.InferredType))])));
        }

        // Ordered by the scope's own document order — upload order — rather than by whichever table the
        // rows came out of, so the list a refusal offers reads the way the conversation does.
        return
        [
            .. spreadsheets.SelectMany(document => records
                .Where(r => r.Source == document.Source && r.DocumentId == document.Id)
                .OrderBy(r => r.SheetIndex)
                .Select(r => new ResolvedSheet(
                    r.Id,
                    r.DocumentId,
                    r.Source,
                    document.Name,
                    r.SheetName,
                    DocumentRetrievalService.BuildCitation(document.Name, r.SheetIndex, r.SheetName),
                    r.RowCount,
                    r.Columns)))
        ];
    }

    private static bool TryResolveSheet(
        IReadOnlyList<ResolvedSheet> sheets,
        string? sheetName,
        string echoed,
        out ResolvedSheet sheet,
        out SheetQueryResult refusal)
    {
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            if (sheets.Count == 1)
            {
                sheet = sheets[0];
                refusal = null!;

                return true;
            }

            sheet = null!;
            refusal = Refusal(
                echoed,
                "More than one sheet is attached, so name the one to query.",
                availableSheets: DescribeSheets(sheets));

            return false;
        }

        var wanted = sheetName.Trim();
        var matches = MatchSheetByName(sheets, wanted);

        if (matches.Count == 1)
        {
            sheet = matches[0];
            refusal = null!;

            return true;
        }

        // Refused rather than widened to whichever sheet came first: search widens on an ambiguous
        // document name because a wider search is cheap, and a total computed over the wrong sheet is a
        // wrong answer with nothing about it to give the mistake away.
        sheet = null!;
        refusal = Refusal(
            echoed,
            matches.Count == 0
                ? $"No sheet is named '{wanted}'. Ask again with one of the available names."
                : $"'{wanted}' matches more than one sheet. Ask again with a full name.",
            availableSheets: DescribeSheets(sheets));

        return false;
    }

    private bool TryResolveQuery(
        SheetQueryOperation operation,
        ResolvedSheet sheet,
        string? column,
        string? groupBy,
        IReadOnlyList<SheetFilter>? filters,
        string echoed,
        out ResolvedQuery query,
        out SheetQueryResult refusal)
    {
        query = null!;
        refusal = null!;

        List<string> notes = [];
        var supplied = filters?.Where(f => f is not null).ToArray() ?? [];

        if (supplied.Length > _options.MaxFilters)
        {
            refusal = Refusal(
                echoed,
                $"Only {_options.MaxFilters} filters can be applied at once, and {supplied.Length} were supplied. "
                    + "Ask again with fewer.",
                sheet: sheet);

            return false;
        }

        var isFilter = operation is SheetQueryOperation.Filter;

        if (isFilter && (!string.IsNullOrWhiteSpace(column) || !string.IsNullOrWhiteSpace(groupBy)))
        {
            notes.Add("The filter operation returns whole rows, so the column and groupBy arguments were ignored.");
        }

        ResolvedSheetColumn? valueColumn = null;
        string? valuePath = null;

        if (!isFilter
            && !string.IsNullOrWhiteSpace(column)
            && !TryResolveColumn(sheet, column.Trim(), echoed, out valueColumn, out valuePath, out refusal))
        {
            return false;
        }

        if (!isFilter && valueColumn is null && operation is not SheetQueryOperation.Count)
        {
            refusal = Refusal(
                echoed,
                $"'{echoed}' needs a column to compute over. Ask again naming one of this sheet's columns.",
                sheet: sheet,
                availableColumns: DescribeColumns(sheet));

            return false;
        }

        ResolvedSheetColumn? groupColumn = null;
        string? groupPath = null;

        if (!isFilter
            && !string.IsNullOrWhiteSpace(groupBy)
            && !TryResolveColumn(sheet, groupBy.Trim(), echoed, out groupColumn, out groupPath, out refusal))
        {
            return false;
        }

        var specs = new List<SheetFilterSpec>(supplied.Length);
        var paths = new List<string>(supplied.Length);
        var values = new List<object>(supplied.Length);

        foreach (var filter in supplied)
        {
            if (string.IsNullOrWhiteSpace(filter.Column))
            {
                refusal = Refusal(
                    echoed,
                    "Every filter needs a column. Ask again naming one of this sheet's columns.",
                    sheet: sheet,
                    availableColumns: DescribeColumns(sheet));

                return false;
            }

            if (!TryParseComparator(filter.Comparator, out var comparator))
            {
                refusal = Refusal(
                    echoed,
                    $"'{filter.Comparator}' is not a comparator. Ask again with one of: eq, neq, gt, gte, lt, lte, "
                        + "contains.",
                    sheet: sheet);

                return false;
            }

            if (!TryResolveColumn(sheet, filter.Column.Trim(), echoed, out var filterColumn, out var filterPath, out refusal))
            {
                return false;
            }

            // A bound parameter silently truncates past its declared size, which for a substring match
            // would drop the trailing wildcard and quietly turn it into a prefix one. Escaping can double
            // a pattern's length, and the two wildcards are added on top of that.
            if (filter.Value is { Length: > (TextParameterLength - 2) / 2 })
            {
                refusal = Refusal(
                    echoed,
                    $"The value supplied for column '{filterColumn!.Name}' is longer than "
                        + $"{(TextParameterLength - 2) / 2} characters and was not compared. Ask again with a "
                        + "shorter one.",
                    sheet: sheet);

                return false;
            }

            if (!TryBindFilterValue(filterColumn!, comparator, filter.Value, out var bound))
            {
                refusal = Refusal(
                    echoed,
                    $"'{filter.Value}' cannot be compared against column '{filterColumn!.Name}', which holds "
                        + $"{Describe(filterColumn.Type)}. Ask again with a value of that kind.",
                    sheet: sheet,
                    availableColumns: DescribeColumns(sheet));

                return false;
            }

            specs.Add(new SheetFilterSpec(filterColumn!.Type, comparator));
            paths.Add(filterPath!);
            values.Add(bound);
        }

        // Sum and average always read the column as a number, whatever a bounded sample inferred it to
        // be: a column with one stray cell among its first rows infers as text and would otherwise be
        // permanently untotallable. Whatever the coercion drops is counted and reported.
        SheetColumnType? valueType = operation switch
        {
            SheetQueryOperation.Sum or SheetQueryOperation.Avg => SheetColumnType.Number,
            SheetQueryOperation.Count or SheetQueryOperation.Filter => valueColumn?.Type,
            _ => valueColumn!.Type
        };

        if (operation is SheetQueryOperation.Sum or SheetQueryOperation.Avg
            && valueColumn!.Type is not SheetColumnType.Number)
        {
            notes.Add(
                $"Column '{valueColumn.Name}' is not uniformly numeric, so only the rows whose value reads as a "
                    + "number are included.");
        }

        query = new ResolvedQuery(
            operation, sheet, valueColumn, valueType, valuePath, groupColumn, groupPath, specs, paths, values, notes);

        return true;
    }

    private bool TryResolveColumn(
        ResolvedSheet sheet,
        string wanted,
        string echoed,
        out ResolvedSheetColumn? column,
        out string? path,
        out SheetQueryResult refusal)
    {
        column = sheet.Columns.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));

        if (column is null)
        {
            path = null;
            refusal = Refusal(
                echoed,
                $"Sheet '{sheet.SheetName}' has no column named '{wanted}'.",
                sheet: sheet,
                availableColumns: DescribeColumns(sheet));

            return false;
        }

        if (!TryBuildJsonPath(column.Name, out var built))
        {
            // The name itself is file content and stays out of the log.
            _logger.LogWarning(
                "Sheet {SheetId} has a column whose name cannot be expressed as a JSON path; it is not queryable.",
                sheet.Id);

            path = null;
            refusal = Refusal(
                echoed,
                $"Column '{column.Name}' contains a character that cannot be used in a lookup, so it cannot be "
                    + "queried. Ask about another column.",
                sheet: sheet,
                availableColumns: DescribeColumns(sheet));

            return false;
        }

        path = built;
        refusal = null!;

        return true;
    }

    #endregion

    #region Execution

    private async Task<SheetQueryResult> ExecuteAsync(
        DocumentRetrievalScope scope, string echoed, ResolvedQuery query, CancellationToken cancellationToken)
    {
        var result = query.Operation switch
        {
            SheetQueryOperation.Filter =>
                await ListRowsAsync(scope, echoed, query, cancellationToken).ConfigureAwait(false),
            _ when query.GroupColumn is not null =>
                await AggregateGroupedAsync(scope, echoed, query, cancellationToken).ConfigureAwait(false),
            _ => await AggregateAsync(scope, echoed, query, cancellationToken).ConfigureAwait(false)
        };

        if (query.Notes.Count > 0)
        {
            result.Note = result.Note is null
                ? string.Join(" ", query.Notes)
                : $"{result.Note} {string.Join(" ", query.Notes)}";
        }

        return result;
    }

    private async Task<SheetQueryResult> AggregateAsync(
        DocumentRetrievalScope scope, string echoed, ResolvedQuery query, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            SheetQuerySql.BuildAggregate(query.Operation, query.ValueType, query.FilterSpecs),
            command => Bind(command, scope, query),
            reader => new AggregateRow(
                null,
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetValue(2)),
            cancellationToken).ConfigureAwait(false);

        var row = rows.Count > 0 ? rows[0] : new AggregateRow(null, 0, 0, null);

        var result = Shape(echoed, query);
        result.MatchedRowCount = row.MatchedRows;
        result.Value = FormatValue(row.Value);
        result.SkippedValues = SkippedValues(query, row.MatchedRows, row.ValueRows);

        // Zero is the answer to a count, not a failure to reach one, and a note saying otherwise is what
        // the model would relay instead of the figure.
        if (row.MatchedRows == 0 && query.Operation is not SheetQueryOperation.Count)
        {
            result.Note = "No rows matched, so there was nothing to compute.";
        }

        return result;
    }

    private async Task<SheetQueryResult> AggregateGroupedAsync(
        DocumentRetrievalScope scope, string echoed, ResolvedQuery query, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            SheetQuerySql.BuildGroupedAggregate(query.Operation, query.ValueType, query.FilterSpecs),
            command =>
            {
                Bind(command, scope, query);

                // One past the cap, so a trimmed result is reported as trimmed rather than silently
                // looking complete.
                command.Parameters.Add(new SqlParameter("@maxGroups", SqlDbType.Int) { Value = _options.MaxGroups + 1 });
            },
            reader => new AggregateRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetValue(3)),
            cancellationToken).ConfigureAwait(false);

        var truncated = rows.Count > _options.MaxGroups;
        AggregateRow[] kept = truncated ? [.. rows.Take(_options.MaxGroups)] : [.. rows];

        var result = Shape(echoed, query);
        result.MatchedRowCount = kept.Sum(r => r.MatchedRows);
        result.SkippedValues = SkippedValues(query, result.MatchedRowCount, kept.Sum(r => r.ValueRows));
        result.Truncated = truncated;
        result.Groups =
        [
            .. kept.Select(r => new SheetQueryGroup
            {
                Group = r.Group,
                Value = FormatValue(r.Value),
                RowCount = r.MatchedRows
            })
        ];

        if (rows.Count == 0)
        {
            result.Note = "No rows matched, so there was nothing to group.";
        }
        else if (truncated)
        {
            result.Note =
                $"Only the first {_options.MaxGroups} groups are shown, and the counts cover those alone. "
                    + "Filter the rows to narrow them.";
        }

        return result;
    }

    private async Task<SheetQueryResult> ListRowsAsync(
        DocumentRetrievalScope scope, string echoed, ResolvedQuery query, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            SheetQuerySql.BuildRowList(query.FilterSpecs),
            command =>
            {
                Bind(command, scope, query);
                command.Parameters.Add(new SqlParameter("@maxRows", SqlDbType.Int) { Value = _options.MaxResultRows });
            },
            reader => new RowListRow(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)),
            cancellationToken).ConfigureAwait(false);

        var result = Shape(echoed, query);
        result.MatchedRowCount = rows.Count > 0 ? rows[0].MatchedRows : 0;

        var kept = new List<SheetQueryRow>(rows.Count);
        var characters = 0;

        foreach (var row in rows)
        {
            // The row cap bounds how many rows come back, not how wide they are: fifty rows of a
            // two-hundred-column export is context the answer then has to compete with. At least one row
            // always survives, so a single very wide row is still shown rather than silently dropped.
            if (kept.Count > 0 && characters + row.Cells.Length > _options.MaxResultCharacters)
            {
                break;
            }

            characters += row.Cells.Length;

            kept.Add(new SheetQueryRow
            {
                RowIndex = row.RowIndex,
                Cells = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Cells) ?? []
            });
        }

        result.Rows = kept;
        result.Truncated = kept.Count < result.MatchedRowCount;

        if (result.MatchedRowCount == 0)
        {
            result.Note = "No rows matched.";
        }
        else if (result.Truncated)
        {
            result.Note =
                $"{kept.Count} of {result.MatchedRowCount} matching rows are shown. Add filters to narrow them.";
        }

        return result;
    }

    private static void Bind(DbCommand command, DocumentRetrievalScope scope, ResolvedQuery query)
    {
        command.Parameters.Add(new SqlParameter("@sheetId", SqlDbType.UniqueIdentifier) { Value = query.Sheet.Id });
        command.Parameters.Add(
            new SqlParameter("@conversationId", SqlDbType.UniqueIdentifier) { Value = scope.ConversationId });
        command.Parameters.Add(
            new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = (object?)scope.ProjectId ?? DBNull.Value });

        if (query.ValuePath is { } valuePath)
        {
            command.Parameters.Add(TextParameter(SheetQuerySql.ValuePathParameter, valuePath));
        }

        if (query.GroupPath is { } groupPath)
        {
            command.Parameters.Add(TextParameter(SheetQuerySql.GroupPathParameter, groupPath));
        }

        for (var i = 0; i < query.FilterSpecs.Count; i++)
        {
            command.Parameters.Add(TextParameter($"{SheetQuerySql.FilterPathPrefix}{i}", query.FilterPaths[i]));
            command.Parameters.Add(ValueParameter($"{SheetQuerySql.FilterValuePrefix}{i}", query.FilterValues[i]));
        }
    }

    private static SqlParameter TextParameter(string name, string value) =>
        new(name, SqlDbType.NVarChar, TextParameterLength) { Value = value };

    private static SqlParameter ValueParameter(string name, object value) => value switch
    {
        double number => new SqlParameter(name, SqlDbType.Float) { Value = number },
        DateTime date => new SqlParameter(name, SqlDbType.DateTime2) { Value = date },
        _ => TextParameter(name, (string)value)
    };

    /// <summary>
    /// Runs one statement on Entity Framework's own connection.
    /// </summary>
    /// <remarks>
    /// Dropped to ADO.NET rather than <c>SqlQueryRaw</c> because EF composes unmapped-type queries into a
    /// wrapping subquery, which a <c>TOP</c> over a computed <c>GROUP BY</c> cannot survive. The
    /// connection, and any ambient transaction, still belong to EF: only a connection this method opened
    /// is closed again.
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
            command.CommandTimeout = _options.TimeoutSeconds;
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

    #region Shaping

    private static SheetQueryResult Shape(string echoed, ResolvedQuery query) => new()
    {
        Operation = echoed,
        DocumentName = query.Sheet.DocumentName,
        SheetName = query.Sheet.SheetName,
        Column = query.ValueColumn?.Name,
        GroupBy = query.GroupColumn?.Name
    };

    private static SheetQueryResult Refusal(
        string echoed,
        string note,
        ResolvedSheet? sheet = null,
        IReadOnlyList<string>? availableSheets = null,
        IReadOnlyList<string>? availableColumns = null) => new()
        {
            Operation = echoed,
            DocumentName = sheet?.DocumentName,
            SheetName = sheet?.SheetName,
            AvailableSheets = availableSheets,
            AvailableColumns = availableColumns,
            Note = note
        };

    /// <summary>
    /// Names every sheet with its shape, so one refusal is enough for the model to ask the right
    /// question next rather than discovering the columns with a second refusal.
    /// </summary>
    private static IReadOnlyList<string> DescribeSheets(IReadOnlyList<ResolvedSheet> sheets)
    {
        List<string> described =
        [
            .. sheets.Take(MaxDescribedSheets).Select(s => string.Create(
                CultureInfo.InvariantCulture,
                $"{s.Label} ({s.RowCount} rows): {string.Join(", ", DescribeColumns(s))}"))
        ];

        if (sheets.Count > MaxDescribedSheets)
        {
            described.Add(string.Create(
                CultureInfo.InvariantCulture, $"…and {sheets.Count - MaxDescribedSheets} more sheets"));
        }

        return described;
    }

    private static IReadOnlyList<string> DescribeColumns(ResolvedSheet sheet)
    {
        List<string> described =
        [
            .. sheet.Columns
                .Take(MaxDescribedColumns)
                .Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Name} ({Describe(c.Type)})"))
        ];

        if (sheet.Columns.Count > MaxDescribedColumns)
        {
            described.Add(string.Create(
                CultureInfo.InvariantCulture, $"…and {sheet.Columns.Count - MaxDescribedColumns} more columns"));
        }

        return described;
    }

    private static string Describe(SheetColumnType type) => type switch
    {
        SheetColumnType.Number => "numbers",
        SheetColumnType.Date => "dates",
        SheetColumnType.Boolean => "true or false values",
        _ => "text"
    };

    /// <summary>
    /// How many matching rows contributed nothing to the aggregate, which is meaningful only where the
    /// aggregate coerced a value at all.
    /// </summary>
    private static int SkippedValues(ResolvedQuery query, int matchedRows, int valueRows) =>
        query.Operation is SheetQueryOperation.Count || query.ValueType is null or SheetColumnType.Text
            ? 0
            : Math.Max(0, matchedRows - valueRows);

    private static object? FormatValue(object? raw) => raw switch
    {
        null or DBNull => null,

        // Binary floating point carries the arithmetic the spreadsheet itself used, and its last few
        // digits are noise the model should not be handed as significance.
        double number => Math.Round(number, 10),

        // A time-only column's values all land on the date SQL Server anchors them to, and reporting that
        // date back would put a year nobody's spreadsheet mentions into the answer.
        DateTime date when date.Date == TimeOnlyEpoch && date.TimeOfDay != TimeSpan.Zero =>
            date.ToString(@"HH\:mm\:ss", CultureInfo.InvariantCulture),
        DateTime date => date.TimeOfDay == TimeSpan.Zero
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => raw
    };

    #endregion

    #region Parsing

    private static bool TryParseOperation(string value, out SheetQueryOperation operation)
    {
        operation = value.ToLowerInvariant() switch
        {
            "sum" or "total" => SheetQueryOperation.Sum,
            "avg" or "average" or "mean" => SheetQueryOperation.Avg,
            "min" => SheetQueryOperation.Min,
            "max" => SheetQueryOperation.Max,
            "count" => SheetQueryOperation.Count,
            "filter" => SheetQueryOperation.Filter,
            _ => default
        };

        return operation is not default(SheetQueryOperation);
    }

    private static bool TryParseComparator(string? value, out SheetFilterComparator comparator)
    {
        comparator = value?.Trim().ToLowerInvariant() switch
        {
            "eq" or "=" or "==" => SheetFilterComparator.Eq,
            "neq" or "!=" or "<>" => SheetFilterComparator.Neq,
            "gt" or ">" => SheetFilterComparator.Gt,
            "gte" or ">=" => SheetFilterComparator.Gte,
            "lt" or "<" => SheetFilterComparator.Lt,
            "lte" or "<=" => SheetFilterComparator.Lte,
            "contains" => SheetFilterComparator.Contains,
            _ => default
        };

        return comparator is not default(SheetFilterComparator);
    }

    private static bool TryBindFilterValue(
        ResolvedSheetColumn column, SheetFilterComparator comparator, string? value, out object bound)
    {
        var text = value ?? string.Empty;

        if (comparator is SheetFilterComparator.Contains)
        {
            // Matched against the cell's own text whatever the column holds, so the value needs no
            // conversion — only its wildcards escaped.
            bound = $"%{DocumentRetrievalService.EscapeLikePattern(text)}%";

            return true;
        }

        switch (column.Type)
        {
            case SheetColumnType.Number:
                if (double.TryParse(
                    text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number))
                {
                    bound = number;

                    return true;
                }

                bound = text;

                return false;

            case SheetColumnType.Date:
                // A cell holding only a time converts to datetime2 as 1900-01-01 plus that time, and the
                // value is free text the model composes, so anything without a date of its own is
                // re-anchored rather than left on today, where every comparison would miss.
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var date))
                {
                    bound = date.Date == DateTime.MinValue.Date ? TimeOnlyEpoch.Add(date.TimeOfDay) : date;

                    return true;
                }

                bound = text;

                return false;

            case SheetColumnType.Boolean:
                // Cells are stored as TRUE and FALSE, and the comparison collation is case-insensitive,
                // so normalizing the model's wording is all this needs.
                bound = text.Trim().ToLowerInvariant() switch
                {
                    "true" or "yes" or "y" or "1" => "true",
                    "false" or "no" or "n" or "0" => "false",
                    _ => text
                };

                return bound is "true" or "false";

            default:
                bound = text;

                return true;
        }
    }

    #endregion

    private sealed record SheetRecord(
        Guid Id,
        Guid DocumentId,
        DocumentSource Source,
        int SheetIndex,
        string SheetName,
        int RowCount,
        IReadOnlyList<ResolvedSheetColumn> Columns);

    private sealed record ResolvedQuery(
        SheetQueryOperation Operation,
        ResolvedSheet Sheet,
        ResolvedSheetColumn? ValueColumn,
        SheetColumnType? ValueType,
        string? ValuePath,
        ResolvedSheetColumn? GroupColumn,
        string? GroupPath,
        IReadOnlyList<SheetFilterSpec> FilterSpecs,
        IReadOnlyList<string> FilterPaths,
        IReadOnlyList<object> FilterValues,
        IReadOnlyList<string> Notes);

    private sealed record AggregateRow(string? Group, int MatchedRows, int ValueRows, object? Value);

    private sealed record RowListRow(int RowIndex, string Cells, int MatchedRows);
}
