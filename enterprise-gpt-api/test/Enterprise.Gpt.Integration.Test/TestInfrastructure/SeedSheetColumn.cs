using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// One column of a seeded sheet.
/// </summary>
public sealed record SeedSheetColumn(string Name, SheetColumnTypes Type);
