import { IconName } from '@shared/icon/icon-names';

/**
 * The glyph and tint for a file name's extension.
 *
 * Here rather than in `domain/` with the attachment types: it maps onto `IconName`,
 * which is the sprite manifest's own union, and that is a presentation concern.
 */
interface FileGlyph {
  readonly icon: IconName;
  readonly tone: 'fail' | 'ok' | 'accent' | 'muted' | 'warn';
}

const BY_EXTENSION: Readonly<Record<string, FileGlyph>> = {
  pdf: { icon: 'bi-file-earmark-pdf', tone: 'fail' },
  xlsx: { icon: 'bi-file-earmark-excel', tone: 'ok' },
  xls: { icon: 'bi-file-earmark-excel', tone: 'ok' },
  docx: { icon: 'bi-file-earmark-word', tone: 'accent' },
  doc: { icon: 'bi-file-earmark-word', tone: 'accent' },
  pptx: { icon: 'bi-file-earmark-ppt', tone: 'warn' },
  csv: { icon: 'bi-filetype-csv', tone: 'accent' },
  md: { icon: 'bi-filetype-md', tone: 'accent' },
  txt: { icon: 'bi-file-earmark-text', tone: 'muted' },
};

const FALLBACK: FileGlyph = { icon: 'bi-file-earmark', tone: 'muted' };

/**
 * The glyph and tint for a file name's extension.
 *
 * Wider than the eight formats the API accepts, because the same chip renders a file the
 * user picked before anything validated it — a `.xlsx` refused as unsupported should
 * still look like a spreadsheet while it says so.
 *
 * Every icon it can return is in the checked-in sprite manifest; the US-108 build
 * check is what keeps that true as this table grows.
 */
export function fileGlyph(fileName: string): FileGlyph {
  const extension = fileName.split('.').pop()?.toLowerCase() ?? '';
  return BY_EXTENSION[extension] ?? FALLBACK;
}
