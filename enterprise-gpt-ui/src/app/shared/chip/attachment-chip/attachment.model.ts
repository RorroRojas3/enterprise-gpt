import { IconName } from '@shared/icon/icon-names';

/**
 * The states an attachment can be in, from frame `4l`.
 *
 * A discriminated union rather than a bag of booleans, so `@switch` over `kind` is
 * exhaustive and "uploading *and* expired" is unrepresentable — the same modelling
 * discipline `withRequestStatus` established for request state, and the same reason:
 * the old client's upload chips got this wrong by tracking flags independently.
 *
 * `failed` and `unsupported` render alike and are deliberately separate arms. One is a
 * job the pipeline could not finish and carries the server's sanitized `errorMessage`;
 * the other is a file this deployment cannot read at all, refused before any request.
 * Only the first offers Retry: re-posting a `.mov` produces the same refusal, and
 * `canRetry` in `core/errors` already records why a control that cannot succeed is
 * worse than no control. Frame `4l` draws Retry on its unsupported example, and this is
 * the one place the app does not follow it — US-802 ties Retry to `Failed`, and US-805
 * asks only that the unsupported chip name the extension and the supported set.
 */
export type AttachmentState =
  | { readonly kind: 'uploading'; readonly percent: number }
  | { readonly kind: 'processing'; readonly subStatus: string }
  | { readonly kind: 'ready' }
  /** Ingestion failed. `reason` is the job's `errorMessage`, sanitized server-side. */
  | { readonly kind: 'failed'; readonly reason: string }
  /** Refused client-side: this deployment has no extractor for the extension. */
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

/**
 * What the chip's `×` actually does, which changes once the file is the server's.
 *
 * While an upload is in flight or a job is queued, `×` cancels — there is something to
 * call off. Once the document is ingested it belongs to the conversation and there is
 * no API to detach it (only project documents have a `DELETE`), so `×` only takes the
 * chip off screen. Naming that control "Remove" at that point would claim an effect it
 * does not have, which is the whole reason this input exists.
 */
export type AttachmentRemoveAction = 'cancel' | 'dismiss';

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
 * Wider than the six formats the API accepts, because the same chip renders a file the
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
