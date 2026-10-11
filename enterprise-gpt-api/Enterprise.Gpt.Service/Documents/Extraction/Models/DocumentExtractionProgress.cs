namespace Enterprise.Gpt.Service.Documents.Extraction.Models;

/// <summary>
/// Best-effort progress report emitted while a document is being read.
/// </summary>
/// <param name="Fraction">
/// 0..1, or <see langword="null"/> when the work cannot be measured (remote OCR); null leaves the
/// percentage where it is and refreshes only <paramref name="Message"/>.
/// </param>
/// <param name="Message">Short and user-facing.</param>
public sealed record DocumentExtractionProgress(double? Fraction, string Message);
