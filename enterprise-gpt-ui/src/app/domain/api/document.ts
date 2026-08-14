/**
 * The 202 body of an upload. Mirrors `Enterprise.Gpt.Dto.JobDto`.
 *
 * The id is a `Guid.ToString()` server-side, but the status route binds `{jobId}` as a
 * bare `string` with no `:guid` constraint, so it is typed as one here and never parsed.
 */
export interface JobDto {
  readonly id: string;
}

/**
 * The four-value vocabulary `DocumentEndpoints.MapState` projects `JobStatus` onto.
 *
 * Only `Succeeded` and `Failed` are terminal. Every stage that is neither queued nor
 * finished — including stages appended to the enum after this contract was set — maps
 * to `Processing`, which is what makes the server free to add stages without breaking
 * a client written today.
 */
export const JOB_STATE = {
  enqueued: 'Enqueued',
  processing: 'Processing',
  succeeded: 'Succeeded',
  failed: 'Failed',
} as const;

export type JobState = (typeof JOB_STATE)[keyof typeof JOB_STATE];

/**
 * One poll of `GET api/documents/upload-status/{jobId}`.
 *
 * Mirrors `Enterprise.Gpt.Dto.JobStatusDto`. Three things about it are easy to get
 * wrong:
 *
 * - **`state` and `status` do not use the same words.** `state` is the coarse
 *   projection above; `status` is the `JobStatus` member name. `state: 'Succeeded'`
 *   pairs with `status: 'Processed'`, and `state: 'Enqueued'` with `status: 'Queued'`.
 *   Branch on `state`; render `status` only as prose.
 * - **`completedUnits`/`totalUnits` are cleared by any report that does not supply
 *   them**, which the countable stages (chunking, embedding) do and the others do not.
 *   A client that latches the last pair it saw will show a stale "3 of 12" through a
 *   stage that is not counting anything.
 * - **`progress` is already monotonic server-side** (`Math.Max` in `JobStatusStore`),
 *   and a failure leaves it where it stopped rather than resetting it.
 *
 * A `404` on this route is *not* modelled here: it means expired, evicted, restarted,
 * or another user's job, and the server deliberately makes those indistinguishable.
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
 * A short-lived signed link from `GET api/documents/{parent}/{parentId}/{documentId}`.
 *
 * Mirrors `Enterprise.Gpt.Dto.DocumentDownloadDto`. {@link downloadUrl} is a **bearer
 * credential** for roughly five minutes: anyone holding it reads the file with no
 * sign-in. It is fetched on click and used immediately, and must never be persisted,
 * logged, placed in a router URL, or held in store state.
 */
export interface DocumentDownloadDto {
  readonly downloadUrl: string;
  /** The name as uploaded. The link already carries it as a `Content-Disposition`. */
  readonly fileName: string;
  /** ISO 8601 with offset. */
  readonly expiresAt: string;
}

/**
 * What a document hangs off.
 *
 * Upload and download are both routed by the parent — `documents/conversations/{id}`
 * and `documents/projects/{id}` — and ownership is read off that parent rather than
 * off the document row. Modelling it as a discriminated pair rather than a bare id is
 * what lets one store serve the composer (US-801) and, later, a project's files panel
 * (US-905) without either learning about the other's routes.
 */
export type UploadTarget =
  | { readonly kind: 'conversation'; readonly id: string }
  | { readonly kind: 'project'; readonly id: string };

/**
 * The `api/`-relative path segment a target's document routes live under.
 *
 * @param target The parent the document belongs to.
 */
export function documentTargetPath(target: UploadTarget): string {
  return target.kind === 'conversation' ? 'conversations' : 'projects';
}
