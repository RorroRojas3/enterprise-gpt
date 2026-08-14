/**
 * A byte count as the design writes it — `50 MB`, `2.4 MB`, `812 KB`.
 *
 * Shared rather than duplicated because two surfaces have to agree digit for digit:
 * the attachment chip's size label and US-805's too-large toast, which reads "Files
 * can be up to 50 MB — this one is 212 MB." A second implementation that rounded
 * differently would make the toast contradict the chip it is complaining about.
 *
 * It lives in `domain/` because `core/errors` consumes it and `core/` may not import
 * `shared/`. Framework-free, so it tests in Node with no TestBed.
 *
 * Deliberately MB/KB only, matching `MaxUploadSizeEndpointFilter.FormatSize` on the
 * server: the upload cap is capped at 500 MB, so a GB branch is unreachable and would
 * only be a place for the two sides to drift.
 *
 * @param bytes A non-negative byte count.
 */
export function formatBytes(bytes: number): string {
  const megabytes = bytes / (1024 * 1024);

  // Whole kilobytes, and one decimal only above a megabyte. The server's own
  // `FormatSize` uses integer division throughout, so this never claims more
  // precision than the limit it is compared against — and "0.0 KB" for a nearly
  // empty file reads as a bug rather than as a size.
  return megabytes >= 1 ? `${round(megabytes)} MB` : `${Math.round(bytes / 1024)} KB`;
}

function round(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}
