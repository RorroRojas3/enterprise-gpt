namespace Enterprise.Gpt.Repository.Sql.Constants;

/// <summary>
/// The names of the model-level query filters, for lifting one by name with <c>IgnoreQueryFilters</c>.
/// </summary>
public static class QueryFilterNames
{
    /// <summary>Hides rows whose <c>DateDeactivated</c> is set.</summary>
    public const string SoftDelete = "SoftDelete";
}
