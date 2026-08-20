/**
 * The export formats the download menu offers, as the `?format=` tokens the API accepts.
 *
 * The API supports two more — `html` and `json` — which predate US-1501 and which no
 * surface offers: they exist for scripted callers, and frame `2f` draws three items.
 */
export const EXPORT_FORMATS = ['md', 'docx', 'pdf'] as const;

/** One of the three formats a conversation can be downloaded as. */
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
