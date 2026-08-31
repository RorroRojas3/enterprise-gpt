/**
 * The export formats the download menu offers, as the `?format=` tokens the API accepts.
 *
 * The API supports one more — `json`, the transcript's own storage shape — which no surface
 * offers and which exists for scripted callers. Listing a format here does not promise the
 * deployment can render it: configuration can withdraw any of them, and PDF and HTML each
 * have a prerequisite, so every row has to survive an `export-renderer-not-configured` 503.
 */
export const EXPORT_FORMATS = ['md', 'docx', 'pdf', 'html'] as const;

export type ExportFormat = (typeof EXPORT_FORMATS)[number];

/**
 * How each format is named to a reader.
 *
 * Keyed by the wire token and typed loosely on purpose: the server can name a format on
 * an `export-renderer-not-configured` problem, and a token this client does not know
 * should degrade to the token rather than to a lookup that cannot compile.
 */
export const EXPORT_FORMAT_LABELS: Readonly<Record<string, string>> = {
  md: 'Markdown (.md)',
  docx: 'Word (.docx)',
  pdf: 'PDF',
  html: 'HTML',
  json: 'JSON',
};
