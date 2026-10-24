import { IconName } from '@shared/utils/icons/icon-names';

/** Bootstrap theme-colour names. */
export type FileGlyphTone = 'danger' | 'success' | 'info' | 'secondary' | 'warning';

/** Emphasis for info and success: their base colours fall under 3:1 on a light surface. */
export const FILE_GLYPH_TEXT: Readonly<Record<FileGlyphTone, string>> = {
  danger: 'text-danger',
  success: 'text-success-emphasis',
  info: 'text-info-emphasis',
  warning: 'text-warning',
  secondary: 'text-body-secondary',
};

/** Not in `attachment.model.ts`: it maps onto `IconName`, which `shared/models` may not import. */
interface FileGlyph {
  readonly icon: IconName;
  readonly tone: FileGlyphTone;
}

const BY_EXTENSION: Readonly<Record<string, FileGlyph>> = {
  pdf: { icon: 'bi-file-earmark-pdf', tone: 'danger' },
  xlsx: { icon: 'bi-file-earmark-excel', tone: 'success' },
  xls: { icon: 'bi-file-earmark-excel', tone: 'success' },
  docx: { icon: 'bi-file-earmark-word', tone: 'info' },
  doc: { icon: 'bi-file-earmark-word', tone: 'info' },
  pptx: { icon: 'bi-file-earmark-ppt', tone: 'warning' },
  csv: { icon: 'bi-filetype-csv', tone: 'info' },
  md: { icon: 'bi-filetype-md', tone: 'info' },
  txt: { icon: 'bi-file-earmark-text', tone: 'secondary' },
};

const FALLBACK: FileGlyph = { icon: 'bi-file-earmark', tone: 'secondary' };

/**
 * Wider than the formats the API accepts: a `.xlsx` refused as unsupported should still look like
 * a spreadsheet. A build check keeps every icon here in the sprite manifest.
 */
export function fileGlyph(fileName: string): FileGlyph {
  const extension = fileName.split('.').pop()?.toLowerCase() ?? '';
  return BY_EXTENSION[extension] ?? FALLBACK;
}
