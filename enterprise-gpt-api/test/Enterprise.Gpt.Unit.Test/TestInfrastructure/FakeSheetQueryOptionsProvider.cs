using Enterprise.Gpt.Service.Settings;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Serves a settable <see cref="SheetQueryOptions"/>, so a test can flip the switch between turns the
/// way a configuration reload does.
/// </summary>
public sealed class FakeSheetQueryOptionsProvider(SheetQueryOptions? options = null) : ISheetQueryOptionsProvider
{
    /// <inheritdoc />
    public SheetQueryOptions Current { get; set; } = options ?? new SheetQueryOptions();
}
