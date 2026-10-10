using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Options;

/// <summary>
/// Sizing for the header-repeating row windows a sheet is emitted as, bound from the
/// <c>Sheets:RowWindow</c> configuration section.
/// </summary>
public sealed class RowWindowOptions
{
    /// <summary>
    /// Share of the chunker's own token budget a window aims for. Default: 0.75.
    /// </summary>
    /// <remarks>
    /// Comfortably under the budget so a window survives chunking as one indivisible unit and keeps
    /// its header; at the budget it would regularly spill and degrade to bare rows.
    /// </remarks>
    [Range(0.1, 1.0)]
    public double TargetTokenFraction { get; set; } = 0.75;

    /// <summary>
    /// Largest number of data rows in one window, whatever the token count. Default: 50.
    /// </summary>
    [Range(1, 1_000)]
    public int MaxRows { get; set; } = 50;
}
