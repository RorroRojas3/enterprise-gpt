namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// A sheet written straight to the tables, so a query test's expected answer matches the stored values.
/// A column a row omits is stored absent, as ingestion stores an empty cell.
/// </summary>
public sealed record SeedSheet(
    int SheetIndex,
    string Name,
    IReadOnlyList<SeedSheetColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);
