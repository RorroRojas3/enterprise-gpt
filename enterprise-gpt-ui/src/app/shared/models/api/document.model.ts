/**
 * The 202 body of an upload. The id is a Guid server-side, but the status route binds
 * `{jobId}` as a bare string, so it is never parsed here.
 */
export interface JobDto {
  readonly id: string;
}

/**
 * `DocumentEndpoints.MapState`'s projection of `JobStatus`. Any stage between queued and
 * finished, including ones added later, maps to `Processing`.
 */
export const JOB_STATE = {
  enqueued: 'Enqueued',
  processing: 'Processing',
  succeeded: 'Succeeded',
  failed: 'Failed',
} as const;

export type JobState = (typeof JOB_STATE)[keyof typeof JOB_STATE];

/**
 * One status poll. Branch on `state`; `status` is the `JobStatus` name and differs from it
 * (`Succeeded` pairs with `Processed`). The unit counts clear on stages that don't count, so
 * never latch them. A 404 (expired, or another user's job) is deliberately not modelled.
 */
export interface JobStatusDto {
  readonly id: string;
  readonly state: JobState;
  /** The fine-grained pipeline stage, for prose only. */
  readonly status: string;
  /** 0–100. Never decreases across polls. */
  readonly progress: number;
  readonly message: string | null;
  readonly completedUnits: number | null;
  readonly totalUnits: number | null;
  /** Populated only once `state` is `Succeeded` — the only way to learn the id. */
  readonly documentId: string | null;
  /** Populated only on failure, and sanitized server-side before it is sent. */
  readonly errorMessage: string | null;
  /** ISO 8601 with offset. */
  readonly updatedAt: string;
}

/**
 * {@link downloadUrl} is a bearer credential for about five minutes: fetch it on click, use it
 * immediately, and never persist, log, route or store it.
 */
export interface DocumentDownloadDto {
  readonly downloadUrl: string;
  /** The name as uploaded. The link already carries it as a `Content-Disposition`. */
  readonly fileName: string;
  /** ISO 8601 with offset. */
  readonly expiresAt: string;
}

/** Sent as its member name, unlike every other enum this API sends. */
export type DocumentType = 'Uploaded' | 'Generated';

/**
 * The `?type=` filter for generated documents. Lower-cased, unlike a row's value: the route is
 * case-insensitive and this is the spelling it advertises.
 */
export const GENERATED_DOCUMENT_TYPE = 'generated';

/**
 * A conversation document from `GET api/documents`, newest first; render the server's order.
 * The wire's duplicate `documentId` alias is deliberately not modelled.
 */
export interface UserDocumentDto {
  readonly id: string;
  readonly conversationId: string;
  /** The original file name. */
  readonly name: string;
  /** Lower-cased, **including the leading dot** — `.pdf`, not `pdf`. */
  readonly extension: string;
  /** The content type the client reported at upload time. */
  readonly mimeType: string;
  /** Bytes. */
  readonly size: number;
  readonly type: DocumentType;
  /** ISO 8601 with offset — when ingestion finished, not when the upload started. */
  readonly dateCreated: string;
  /** The owning conversation's name — present only on the cross-conversation route. */
  readonly conversationName: string;
  /**
   * The owning conversation's documents matching this listing's filters, so a grouped client
   * can say "3 of 8" without having paged to them all.
   */
  readonly conversationDocumentCount: number;
}

/**
 * What a document hangs off. Routes and ownership go through the parent, so one store serves
 * both the composer and a project's files panel.
 */
export type UploadTarget =
  | { readonly kind: 'conversation'; readonly id: string }
  | { readonly kind: 'project'; readonly id: string };
