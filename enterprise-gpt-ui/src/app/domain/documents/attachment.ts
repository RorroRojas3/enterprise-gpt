/**
 * What an attachment is, independent of anything that renders one.
 *
 * In `domain/` rather than beside the chip because two layers need these types and the
 * chip is not one of them twice over: `core/documents/upload-store.ts` holds the state
 * machine, and `shared/chip/attachment-chip` draws it. `core → shared` is not a
 * direction this app has, and these are plain data — no `IconName`, no Angular. The
 * glyph table, which does need one, stayed with the chip.
 */
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
 * call off, and a job already accepted is undone by deleting what it goes on to create.
 * Once the document is ingested the chip only hides it: both parents have a delete
 * route, but a finished attachment is deliberately not detachable from here. Naming that
 * control "Remove" at that point would claim an effect it does not have, which is the
 * whole reason this input exists.
 */
export type AttachmentRemoveAction = 'cancel' | 'dismiss';
