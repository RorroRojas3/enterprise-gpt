/**
 * `50 MB`, `2.4 MB`, `812 KB`. Shared so the chip and the too-large toast agree to the digit.
 * MB/KB only, matching the server's `FormatSize`: the cap is at most 500 MB.
 */
export function formatBytes(bytes: number): string {
  const megabytes = bytes / (1024 * 1024);

  // Whole kilobytes and one decimal above a megabyte: no more precision than the server's
  // integer `FormatSize`, and never "0.0 KB".
  return megabytes >= 1 ? `${round(megabytes)} MB` : `${Math.round(bytes / 1024)} KB`;
}

function round(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}
