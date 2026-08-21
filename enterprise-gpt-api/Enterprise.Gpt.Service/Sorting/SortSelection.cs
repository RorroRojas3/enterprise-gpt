namespace Enterprise.Gpt.Service.Sorting;

/// <summary>
/// A resolved ordering: the field a listing sorts on and the direction it runs in.
/// </summary>
/// <typeparam name="TKey">The listing's sort-field enumeration.</typeparam>
/// <param name="Key">The field to sort on.</param>
/// <param name="Direction">The direction to sort in.</param>
public readonly record struct SortSelection<TKey>(TKey Key, SortDirection Direction)
    where TKey : struct, Enum
{
    /// <summary>
    /// Gets a value that indicates whether the ordering runs lowest value first.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="Direction"/> is
    /// <see cref="SortDirection.Ascending"/>; otherwise <see langword="false"/>.
    /// </value>
    public bool IsAscending => Direction == SortDirection.Ascending;
}
