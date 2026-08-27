using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Ingest-time ceilings for spreadsheet extraction, bound from the <c>Sheets</c> configuration section
/// and validated at startup.
/// </summary>
/// <remarks>
/// A sheet past either ceiling is refused rather than truncated, because a silently shortened sheet
/// makes every later answer about it quietly wrong. The permitted ranges are Excel's own grid limits.
/// </remarks>
public sealed class SheetOptions
{
    /// <summary>
    /// Configuration section this type binds from.
    /// </summary>
    public const string SectionName = "Sheets";

    /// <summary>
    /// Largest number of populated rows accepted from a single sheet. Default: 20,000.
    /// </summary>
    [Range(1, 1_048_576)]
    public int MaxRowsPerSheet { get; set; } = 20_000;

    /// <summary>
    /// Largest number of columns accepted from a single sheet. Default: 200.
    /// </summary>
    [Range(1, 16_384)]
    public int MaxColumnsPerSheet { get; set; } = 200;

    /// <summary>
    /// Largest total number of characters of cell text accepted from one upload, shared strings
    /// included. Default: 8,000,000.
    /// </summary>
    /// <remarks>
    /// The row and column ceilings bound a sheet's grid, not the text it yields, and a workbook is a
    /// compressed archive whose cells expand by three orders of magnitude. This is what makes the upload
    /// size limit a real bound on what one file can cost the host, and it also fixes how far a package
    /// may inflate before it is opened at all.
    /// </remarks>
    [Range(1_024, 512_000_000)]
    public int MaxCharactersPerUpload { get; set; } = 8_000_000;
}
