using System.Text;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// The shape of one filter, with the value it compares against deliberately absent.
/// </summary>
/// <remarks>
/// Only a column's already-resolved type and a comparator drawn from a closed set reach statement
/// construction; the value itself never does, which is what makes every generated command text a
/// function of shape alone.
/// </remarks>
internal readonly record struct SheetFilterSpec(SheetColumnTypes ColumnType, SheetFilterComparators Comparator);
