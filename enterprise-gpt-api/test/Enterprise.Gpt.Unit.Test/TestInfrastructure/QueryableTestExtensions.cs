using Enterprise.Gpt.Repository.Sql.Constants;
using Microsoft.EntityFrameworkCore;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Lifts the soft-delete filter for arrange and assert reads, which must see deactivated rows as they are stored.
/// </summary>
internal static class QueryableTestExtensions
{
    public static IQueryable<T> IncludingDeactivated<T>(this IQueryable<T> source)
        where T : class =>
        source.IgnoreQueryFilters([QueryFilterNames.SoftDelete]);
}
