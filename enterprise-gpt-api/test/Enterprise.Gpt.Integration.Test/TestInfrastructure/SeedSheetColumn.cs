using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

public sealed record SeedSheetColumn(string Name, SheetColumnTypes Type);
