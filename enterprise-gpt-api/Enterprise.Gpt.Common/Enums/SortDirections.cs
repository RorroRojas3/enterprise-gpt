namespace Enterprise.Gpt.Service.Sorting;

/// <summary>
/// The direction a sorted listing runs in.
/// </summary>
public enum SortDirection
{
    /// <summary>
    /// Lowest value first: earliest date, <c>A</c> before <c>Z</c>, <see langword="false"/> before
    /// <see langword="true"/>.
    /// </summary>
    Ascending,

    /// <summary>
    /// Highest value first: latest date, <c>Z</c> before <c>A</c>, <see langword="true"/> before
    /// <see langword="false"/>.
    /// </summary>
    Descending
}
