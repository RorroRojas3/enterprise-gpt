/** In `domain/` because `core/` (the upload store) and `shared/` (the chip) both need it. */
/**
 * `failed` and `unsupported` render alike, but only `failed` offers Retry: re-posting a file
 * this deployment cannot read would get the same refusal.
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
 * `×` cancels an upload or queued job; once ingested it only hides the chip, since a finished
 * attachment is deliberately not deleted from here, so its label must not say "Remove".
 */
export type AttachmentRemoveAction = 'cancel' | 'dismiss';
