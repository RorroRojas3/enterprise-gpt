using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Sorting.Models;

/// <summary>A resolved ordering: the field a listing sorts on and the direction it runs in.</summary>
public readonly record struct SortSelection<TKey>(TKey Key, SortDirections Direction)
    where TKey : struct, Enum
{
    public bool IsAscending => Direction == SortDirections.Ascending;
}
