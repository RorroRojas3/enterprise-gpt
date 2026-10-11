using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Documents.Retrieval;
using Enterprise.Gpt.Service.Documents.Retrieval.Models;
using Enterprise.Gpt.Service.Documents.Sheets.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Documents.Sheets;

/// <summary>
/// Computes exact answers over the cells of an ingested spreadsheet.
/// </summary>
public interface ISheetQueryService
{
    /// <summary>
    /// Aggregates or filters one sheet's rows; a <see langword="null"/> sheet name means the only sheet
    /// in scope.
    /// </summary>
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
    /// <remarks>
    /// A spreadsheet extension is not enough: a file uploaded before the sheet tables existed has chunks
    /// and no rows, so advertising it would promise a workbook every call then denies.
    /// </remarks>
    Task<bool> HasQueryableSheetsAsync(DocumentRetrievalScope scope, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// Every model-supplied name resolves against the caller's own sheet rows before any SQL is built, and the
/// turn's scope is the only document source, so no sheet outside <c>document_search</c>'s reach is queryable.
/// </remarks>
public sealed partial class SheetQueryService(
    ILogger<SheetQueryService> logger,
    ISheetQueryOptionsProvider options,
    EnterpriseGptDbContext ctx) : ISheetQueryService
{
    /// <summary>The widest value <c>JSON_VALUE</c> returns without an explicit return type.</summary>
    private const int TextParameterLength = 4000;

    /// <summary>
    /// Refusals are the one reply no <see cref="SheetQueryOptions"/> cap bounds, and a sheet may hold
    /// hundreds of columns.
    /// </summary>
    private const int MaxDescribedColumns = 24;

    private const int MaxDescribedSheets = 20;

    /// <summary>
    /// The date SQL Server anchors a time-only value to when converting it to <c>datetime2</c>.
    /// </summary>
    private static readonly DateTime TimeOnlyEpoch = new(1900, 1, 1);

    private readonly ILogger<SheetQueryService> _logger = logger;
    private readonly SheetQueryOptions _options = options.Current;
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
                    s => conversationDocumentIds.Contains(s.ConversationDocumentId),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return true;
        }

        return projectDocumentIds.Length > 0
            && await _ctx.ProjectDocumentSheets
                .AnyAsync(
                    s => projectDocumentIds.Contains(s.ProjectDocumentId),
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private sealed record SheetRecord(
        Guid Id,
        Guid DocumentId,
        DocumentSources Source,
        int SheetIndex,
        string SheetName,
        int RowCount,
        IReadOnlyList<ResolvedSheetColumn> Columns);

    private sealed record ResolvedQuery(
        SheetQueryOperations Operation,
        ResolvedSheet Sheet,
        ResolvedSheetColumn? ValueColumn,
        SheetColumnTypes? ValueType,
        string? ValuePath,
        ResolvedSheetColumn? GroupColumn,
        string? GroupPath,
        IReadOnlyList<SheetFilterSpec> FilterSpecs,
        IReadOnlyList<string> FilterPaths,
        IReadOnlyList<object> FilterValues,
        IReadOnlyList<string> Notes);

    private sealed record AggregateRow(string? Group, int MatchedRows, int ValueRows, object? Value);

    private sealed record RowListRow(int RowIndex, string Cells, int MatchedRows);

    #region Private methods

    private static (Guid[] Conversation, Guid[] Project) SpreadsheetIds(DocumentRetrievalScope scope)
    {
        var spreadsheets = scope.Documents
            .Where(d => DocumentRetrievalService.SpreadsheetExtensions.Contains(Path.GetExtension(d.Name)))
            .ToArray();

        return (
            [.. spreadsheets.Where(d => d.Source is DocumentSources.Conversation).Select(d => d.Id)],
            [.. spreadsheets.Where(d => d.Source is DocumentSources.Project).Select(d => d.Id)]);
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
                .Where(s => conversationDocumentIds.Contains(s.ConversationDocumentId))
                .Select(s => new
                {
                    s.Id,
                    DocumentId = s.ConversationDocumentId,
                    s.SheetIndex,
                    s.SheetName,
                    s.RowCount,
                    Columns = s.Columns
                        .OrderBy(c => c.ColumnIndex)
                        .Select(c => new { c.ColumnName, c.InferredType })
                        .ToList()
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            records.AddRange(sheets.Select(s => new SheetRecord(
                s.Id,
                s.DocumentId,
                DocumentSources.Conversation,
                s.SheetIndex,
                s.SheetName,
                s.RowCount,
                [.. s.Columns.Select(c => new ResolvedSheetColumn(c.ColumnName, c.InferredType))])));
        }

        if (projectDocumentIds.Length > 0)
        {
            var sheets = await _ctx.ProjectDocumentSheets
                .AsNoTracking()
                .Where(s => projectDocumentIds.Contains(s.ProjectDocumentId))
                .Select(s => new
                {
                    s.Id,
                    DocumentId = s.ProjectDocumentId,
                    s.SheetIndex,
                    s.SheetName,
                    s.RowCount,
                    Columns = s.Columns
                        .OrderBy(c => c.ColumnIndex)
                        .Select(c => new { c.ColumnName, c.InferredType })
                        .ToList()
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            records.AddRange(sheets.Select(s => new SheetRecord(
                s.Id,
                s.DocumentId,
                DocumentSources.Project,
                s.SheetIndex,
                s.SheetName,
                s.RowCount,
                [.. s.Columns.Select(c => new ResolvedSheetColumn(c.ColumnName, c.InferredType))])));
        }

        // Upload order, not table order, so a refusal's sheet list reads the way the conversation does.
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

        // Refused rather than widened like search: a total over the wrong sheet is a wrong answer with
        // nothing to give the mistake away.
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
        SheetQueryOperations operation,
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

        var isFilter = operation is SheetQueryOperations.Filter;

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

        if (!isFilter && valueColumn is null && operation is not SheetQueryOperations.Count)
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

            // A parameter silently truncates past its size, dropping the trailing wildcard; escaping can
            // double the length, plus the two wildcards.
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

        // Sum and average coerce to number whatever the sampled type, or one stray early cell would make a
        // column untotallable; dropped values are counted and reported.
        var valueType = operation switch
        {
            SheetQueryOperations.Sum or SheetQueryOperations.Avg => SheetColumnTypes.Number,
            SheetQueryOperations.Count or SheetQueryOperations.Filter => valueColumn?.Type,
            _ => valueColumn!.Type
        };

        if (operation is SheetQueryOperations.Sum or SheetQueryOperations.Avg
            && valueColumn!.Type is not SheetColumnTypes.Number)
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
            LogUnqueryableColumn(_logger, sheet.Id);

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

    private async Task<SheetQueryResult> ExecuteAsync(
        DocumentRetrievalScope scope, string echoed, ResolvedQuery query, CancellationToken cancellationToken)
    {
        var result = query.Operation switch
        {
            SheetQueryOperations.Filter =>
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

        // Zero is a count's answer; a note here would be relayed instead of the figure.
        if (row.MatchedRows == 0 && query.Operation is not SheetQueryOperations.Count)
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

                // One past the cap, so truncation is detectable.
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
            // The row cap does not bound width, so characters are capped too; the first row always
            // survives so a single very wide row is still shown.
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
    /// ADO.NET rather than <c>SqlQueryRaw</c>, which wraps the query in a subquery that a <c>TOP</c> over a
    /// computed <c>GROUP BY</c> cannot survive. The connection stays EF's: only one opened here is closed.
    /// </summary>
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
            Note = note,
            IsRefusal = true
        };

    /// <summary>Includes each sheet's columns so one refusal suffices for the model's next attempt.</summary>
    private static List<string> DescribeSheets(IReadOnlyList<ResolvedSheet> sheets)
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

    private static List<string> DescribeColumns(ResolvedSheet sheet)
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

    private static string Describe(SheetColumnTypes type) => type switch
    {
        SheetColumnTypes.Number => "numbers",
        SheetColumnTypes.Date => "dates",
        SheetColumnTypes.Boolean => "true or false values",
        _ => "text"
    };

    /// <summary>Matching rows the aggregate dropped; meaningful only where it coerced a value.</summary>
    private static int SkippedValues(ResolvedQuery query, int matchedRows, int valueRows) =>
        query.Operation is SheetQueryOperations.Count || query.ValueType is null or SheetColumnTypes.Text
            ? 0
            : Math.Max(0, matchedRows - valueRows);

    private static object? FormatValue(object? raw) => raw switch
    {
        null or DBNull => null,

        // The last binary floating-point digits are noise the model should not read as significance.
        double number => Math.Round(number, 10),

        // Time-only values sit on SQL Server's anchor date; reporting it would invent a year.
        DateTime date when date.Date == TimeOnlyEpoch && date.TimeOfDay != TimeSpan.Zero =>
            date.ToString(@"HH\:mm\:ss", CultureInfo.InvariantCulture),
        DateTime date => date.TimeOfDay == TimeSpan.Zero
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => raw
    };

    private static bool TryParseComparator(string? value, out SheetFilterComparators comparator)
    {
        comparator = value?.Trim().ToLowerInvariant() switch
        {
            "eq" or "=" or "==" => SheetFilterComparators.Eq,
            "neq" or "!=" or "<>" => SheetFilterComparators.Neq,
            "gt" or ">" => SheetFilterComparators.Gt,
            "gte" or ">=" => SheetFilterComparators.Gte,
            "lt" or "<" => SheetFilterComparators.Lt,
            "lte" or "<=" => SheetFilterComparators.Lte,
            "contains" => SheetFilterComparators.Contains,
            _ => default
        };

        return comparator is not default(SheetFilterComparators);
    }

    private static bool TryBindFilterValue(
        ResolvedSheetColumn column, SheetFilterComparators comparator, string? value, out object bound)
    {
        var text = value ?? string.Empty;

        if (comparator is SheetFilterComparators.Contains)
        {
            // Matched against the cell text whatever the column type, so only wildcards need escaping.
            bound = $"%{DocumentRetrievalService.EscapeLikePattern(text)}%";

            return true;
        }

        switch (column.Type)
        {
            case SheetColumnTypes.Number:
                if (double.TryParse(
                    text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number))
                {
                    bound = number;

                    return true;
                }

                bound = text;

                return false;

            case SheetColumnTypes.Date:
                // Time-only cells convert onto 1900-01-01, so a value without a date is re-anchored there
                // rather than left on today, where every comparison would miss.
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var date))
                {
                    bound = date.Date == DateTime.MinValue.Date ? TimeOnlyEpoch.Add(date.TimeOfDay) : date;

                    return true;
                }

                bound = text;

                return false;

            case SheetColumnTypes.Boolean:
                // Stored as TRUE/FALSE under a case-insensitive collation; normalizing the wording suffices.
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

    #region Public static methods

    /// <summary>
    /// Matches sheet names or document-qualified labels, widening only as far as needed; the label is what
    /// tells two workbooks' <c>Sheet1</c> apart.
    /// </summary>
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

    /// <summary>Renders a column name as a JSON path, or reports that it cannot be.</summary>
    /// <remarks>
    /// SQL Server does not document escaping inside a quoted member name, so quotes, backslashes and control
    /// characters are refused rather than risk a path that parses as something else.
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

    /// <summary>
    /// Maps the model's operation text onto the closed set, so callers need not trust the echoed string.
    /// </summary>
    internal static bool TryParseOperation(string? value, out SheetQueryOperations operation)
    {
        operation = value?.Trim().ToLowerInvariant() switch
        {
            "sum" or "total" => SheetQueryOperations.Sum,
            "avg" or "average" or "mean" => SheetQueryOperations.Avg,
            "min" => SheetQueryOperations.Min,
            "max" => SheetQueryOperations.Max,
            "count" => SheetQueryOperations.Count,
            "filter" => SheetQueryOperations.Filter,
            _ => default
        };

        return operation is not default(SheetQueryOperations);
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sheet {SheetId} has a column whose name cannot be expressed as a JSON path; it is not queryable.")]
    private static partial void LogUnqueryableColumn(ILogger logger, Guid sheetId);

    #endregion
}
