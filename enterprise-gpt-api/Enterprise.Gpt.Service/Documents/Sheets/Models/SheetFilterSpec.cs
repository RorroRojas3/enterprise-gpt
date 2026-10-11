using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// The shape of one filter, with the value it compares against deliberately absent.
/// </summary>
/// <remarks>
/// Only a resolved column type and a closed-set comparator reach statement construction, never the value,
/// so generated command text is a function of shape alone.
/// </remarks>
internal readonly record struct SheetFilterSpec(SheetColumnTypes ColumnType, SheetFilterComparators Comparator);
