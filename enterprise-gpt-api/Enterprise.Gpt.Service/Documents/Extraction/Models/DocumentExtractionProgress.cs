using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Dto;

namespace Enterprise.Gpt.Service.Documents.Extraction.Models;

/// <summary>
/// Best-effort progress report emitted while a document is being read.
/// </summary>
/// <param name="Fraction">
/// How far through the extraction the reader is, in the range 0..1, or <see langword="null"/> when the
/// underlying operation cannot be measured (for example a remote OCR call, which is opaque until it
/// completes). A <see langword="null"/> fraction leaves the reported percentage where it is and
/// refreshes only <paramref name="Message"/>, so the percentage never claims progress it cannot know.
/// </param>
/// <param name="Message">A short, user-facing description of what is happening right now.</param>
public sealed record DocumentExtractionProgress(double? Fraction, string Message);
