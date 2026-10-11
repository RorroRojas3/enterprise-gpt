using Enterprise.Gpt.Service.Documents.Sheets;
using Enterprise.Gpt.Service.Options;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Serves a settable <see cref="SheetQueryOptions"/>, so a test can flip the switch between turns the
/// way a configuration reload does.
/// </summary>
public sealed class FakeSheetQueryOptionsProvider(SheetQueryOptions? options = null) : ISheetQueryOptionsProvider
{
    /// <inheritdoc />
    public SheetQueryOptions Current { get; } = options ?? new SheetQueryOptions();
}
