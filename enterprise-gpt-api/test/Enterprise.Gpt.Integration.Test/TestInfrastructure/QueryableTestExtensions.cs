using Enterprise.Gpt.Repository.Sql.Constants;
using Microsoft.EntityFrameworkCore;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Lifts the soft-delete filter for arrange, assert and reset statements, which must reach deactivated rows too.
/// </summary>
internal static class QueryableTestExtensions
{
    public static IQueryable<T> IncludingDeactivated<T>(this IQueryable<T> source)
        where T : class =>
        source.IgnoreQueryFilters([QueryFilterNames.SoftDelete]);
}
