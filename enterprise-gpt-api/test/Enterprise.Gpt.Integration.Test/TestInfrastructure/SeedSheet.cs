using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// One column of a seeded sheet.
/// </summary>
public sealed record SeedSheetColumn(string Name, SheetColumnType Type);

/// <summary>
/// A sheet to insert directly, with its cells given as ordinary dictionaries.
/// </summary>
/// <remarks>
/// Written straight to the tables rather than run through the extractor, because a query test needs the
/// stored values to be the ones it computed its expected answer from. A column a row leaves out is
/// stored absent, exactly as ingestion writes an empty cell.
/// </remarks>
public sealed record SeedSheet(
    int SheetIndex,
    string Name,
    IReadOnlyList<SeedSheetColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);
