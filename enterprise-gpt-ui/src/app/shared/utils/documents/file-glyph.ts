import { IconName } from '@shared/icon/icon-names';

/** Here, not in `domain/`: it maps onto `IconName`, the sprite manifest's union. */
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
 * Wider than the formats the API accepts: a `.xlsx` refused as unsupported should still look like
 * a spreadsheet. A build check keeps every icon here in the sprite manifest.
 */
export function fileGlyph(fileName: string): FileGlyph {
  const extension = fileName.split('.').pop()?.toLowerCase() ?? '';
  return BY_EXTENSION[extension] ?? FALLBACK;
}
