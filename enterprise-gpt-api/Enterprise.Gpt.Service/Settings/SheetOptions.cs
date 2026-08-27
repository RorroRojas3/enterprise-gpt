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
    /// Largest number of populated rows accepted from one upload, across every sheet in it.
    /// Default: 50,000.
    /// </summary>
    /// <remarks>
    /// Nothing bounds how many sheets a workbook holds, so the per-sheet ceiling alone lets a
    /// hundred narrow sheets stay under the character ceiling while turning into millions of rows in
    /// the one save that persists a document.
    /// </remarks>
    [Range(1, 1_048_576)]
    public int MaxRowsPerUpload { get; set; } = 50_000;

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

    /// <summary>
    /// How a sheet's data rows are packed into retrievable windows.
    /// </summary>
    public RowWindowOptions RowWindow { get; set; } = new();
}

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
