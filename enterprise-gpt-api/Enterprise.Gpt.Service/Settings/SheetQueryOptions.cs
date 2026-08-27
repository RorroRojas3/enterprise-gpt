using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Bounds for the deterministic spreadsheet lookup tool, bound from the <c>SheetQuery</c>
/// configuration section and validated at startup.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>Documents:Retrieval</c>'s own caps: this tool returns structured
/// rows rather than prose passages, and it scans a whole sheet rather than a narrowed candidate set,
/// so neither its size nor its time budget follows from retrieval's.
/// </remarks>
public sealed class SheetQueryOptions
{
    /// <summary>
    /// Configuration section this type binds from.
    /// </summary>
    public const string SectionName = "SheetQuery";

    /// <summary>
    /// Gets or sets a value indicating whether the tool is attached to turns.
    /// Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Off unless a deployment asks for it, and flipping it takes effect on the next turn with no
    /// redeploy. Upload, extraction, chunking and storage for spreadsheets are unaffected either way.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Largest number of rows one filter result may return. Default: 50.
    /// </summary>
    [Range(1, 500)]
    public int MaxResultRows { get; set; } = 50;

    /// <summary>
    /// Largest number of characters of cell text one filter result may return. Default: 20,000.
    /// </summary>
    /// <remarks>
    /// The row cap alone bounds nothing about width: fifty rows of a two-hundred-column export is
    /// tens of thousands of characters competing with the answer for the model's context window.
    /// </remarks>
    [Range(1_000, 200_000)]
    public int MaxResultCharacters { get; set; } = 20_000;

    /// <summary>
    /// Largest number of groups a grouped aggregate may return. Default: 200.
    /// </summary>
    [Range(1, 2_000)]
    public int MaxGroups { get; set; } = 200;

    /// <summary>
    /// Largest number of filters one call may supply. Default: 5.
    /// </summary>
    /// <remarks>
    /// Each filter is one more comparison arm evaluated per row of the sheet, and a request past
    /// this is refused rather than trimmed — silently dropping a filter would return rows the model
    /// believes it excluded.
    /// </remarks>
    [Range(1, 20)]
    public int MaxFilters { get; set; } = 5;

    /// <summary>
    /// Per-statement command timeout, in seconds. Default: 10.
    /// </summary>
    /// <remarks>
    /// The tool runs inside a live chat turn, so a degraded query has to fail fast rather than hold
    /// the stream open behind the provider's own timeout.
    /// </remarks>
    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Deadline for one whole tool invocation, in seconds. Default: 30.
    /// </summary>
    /// <remarks>
    /// Bounds name resolution and the statement together, so it must be at least
    /// <see cref="TimeoutSeconds"/> — a call deadline shorter than its own statement's timeout would
    /// abandon every query before that timeout could report one, which reads as a broken tool rather
    /// than a slow database. Startup validation enforces it.
    /// </remarks>
    [Range(1, 300)]
    public int ToolTimeoutSeconds { get; set; } = 30;
}
