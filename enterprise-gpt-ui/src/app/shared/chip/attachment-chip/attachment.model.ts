import { IconName } from '@shared/icon/icon-names';

/**
 * The five states an attachment can be in, from frame `4l`.
 *
 * A discriminated union rather than five booleans, so `@switch` over `kind` is
 * exhaustive and "uploading *and* expired" is unrepresentable — the same modelling
 * discipline `withRequestStatus` established for request state, and the same reason:
 * the old client's upload chips got this wrong by tracking flags independently.
 */
export type AttachmentState =
  | { readonly kind: 'uploading'; readonly percent: number }
  | { readonly kind: 'processing'; readonly subStatus: string }
  | { readonly kind: 'ready' }
  | { readonly kind: 'unsupported'; readonly reason: string }
  /** A 404 on the status endpoint after the retention window. Not a failure. */
  | { readonly kind: 'unknown' };

export interface Attachment {
  readonly id: string;
  readonly fileName: string;
  /** Pre-formatted by the caller — the kit does not decide units. */
  readonly sizeLabel: string | null;
  readonly state: AttachmentState;
}

interface FileGlyph {
  readonly icon: IconName;
  readonly tone: 'fail' | 'ok' | 'accent' | 'muted';
}

const BY_EXTENSION: Readonly<Record<string, FileGlyph>> = {
  pdf: { icon: 'bi-file-earmark-pdf', tone: 'fail' },
  xlsx: { icon: 'bi-file-earmark-excel', tone: 'ok' },
  xls: { icon: 'bi-file-earmark-excel', tone: 'ok' },
  docx: { icon: 'bi-file-earmark-word', tone: 'accent' },
  doc: { icon: 'bi-file-earmark-word', tone: 'accent' },
  csv: { icon: 'bi-filetype-csv', tone: 'accent' },
  md: { icon: 'bi-filetype-md', tone: 'accent' },
  txt: { icon: 'bi-file-earmark-text', tone: 'muted' },
};

const FALLBACK: FileGlyph = { icon: 'bi-file-earmark', tone: 'muted' };

/**
 * The glyph and tint for a file name's extension.
 *
 * Every icon it can return is in the checked-in sprite manifest; the US-108 build
 * check is what keeps that true as this table grows.
 */
export function fileGlyph(fileName: string): FileGlyph {
  const extension = fileName.split('.').pop()?.toLowerCase() ?? '';
  return BY_EXTENSION[extension] ?? FALLBACK;
}
