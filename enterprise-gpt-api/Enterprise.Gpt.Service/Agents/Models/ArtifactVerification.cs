using System.Globalization;
using System.Text;
using CsvHelper.Configuration;
using CsvHelper;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml;
using Microsoft.Extensions.Logging;
using Paragraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using PdfSharp.Pdf.IO;
using Sheet = DocumentFormat.OpenXml.Spreadsheet.Sheet;
using Table = DocumentFormat.OpenXml.Wordprocessing.Table;

namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>What re-opening a produced artifact found.</summary>
/// <param name="Passed">Whether the file opened and carries something.</param>
/// <param name="Shape">
/// What was measured — sheet count, slide count, page count, header row — for the answer to state.
/// Empty on a failure.
/// </param>
/// <param name="Reason">Why it failed, in a sentence safe to relay. <see langword="null"/> on a pass.</param>
public sealed record ArtifactVerification(bool Passed, string Shape, string? Reason)
{
    /// <summary>A file that opened, with what was measured in it.</summary>
    /// <param name="shape">The measurement, for the answer to quote.</param>
    /// <returns>A passing verdict.</returns>
    public static ArtifactVerification Pass(string shape) => new(true, shape, null);

    /// <summary>A file that did not.</summary>
    /// <param name="reason">A clause that completes "… could not be saved because …".</param>
    /// <returns>A failing verdict.</returns>
    public static ArtifactVerification Fail(string reason) => new(false, string.Empty, reason);
}
