/**
 * The `?format=` tokens the download menu offers (`json` is for scripted callers). Configuration
 * can withdraw any of them, so every row must survive an `export-renderer-not-configured` 503.
 */
export const EXPORT_FORMATS = ['md', 'docx', 'pdf', 'html'] as const;

export type ExportFormat = (typeof EXPORT_FORMATS)[number];

/** Loosely typed: the server may name a format this build lacks, which shows as its token. */
export const EXPORT_FORMAT_LABELS: Readonly<Record<string, string>> = {
  md: 'Markdown (.md)',
  docx: 'Word (.docx)',
  pdf: 'PDF',
  html: 'HTML',
  json: 'JSON',
};
