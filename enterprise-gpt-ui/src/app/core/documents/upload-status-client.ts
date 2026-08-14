import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { EMPTY, Observable, catchError, expand, filter, map, of, switchMap, timer } from 'rxjs';
import { JOB_STATE, JobStatusDto } from '@domain/api/document';
import { pollDelayMs } from '@domain/documents/upload-schedule';
import { toAppError } from '@core/errors/to-app-error';
import { ApiUrl } from '@core/http/api-url';

/** What one poll of an upload job produced. */
export type JobPoll =
  | { readonly kind: 'status'; readonly status: JobStatusDto }
  /**
   * A 404. The job is expired, evicted by a restart, or someone else's — the server
   * deliberately makes those indistinguishable, so this arm does too (US-803).
   */
  | { readonly kind: 'expired' };

/** One step of the schedule: how many polls have run, and what the last one said. */
interface PollStep {
  readonly attempt: number;
  readonly poll: JobPoll | null;
}

/**
 * Follows one upload job to a conclusion (US-802).
 *
 * The schedule is staged rather than fixed — see `pollDelayMs`. The stream emits every
 * poll and completes on the first terminal one, so a consumer renders each step and
 * needs no stop condition of its own; anything other than a 404 errors out.
 *
 * `GET` is idempotent, so `retryInterceptor` already replays a 502/503/504 with
 * jittered backoff underneath this, which is why nothing here retries.
 */
@Injectable({ providedIn: 'root' })
export class UploadStatusClient {
  private readonly _http = inject(HttpClient);
  private readonly _apiUrl = inject(ApiUrl);

  /**
   * Polls a job until it succeeds, fails, or turns out to be unknown.
   *
   * `expand` rather than a self-referential `concat`, which is the obvious shape and
   * the wrong one: each recursive level would stay subscribed until the level below
   * it completed, so a twenty-minute OCR job at the five-second ceiling would retain
   * a chain of ~240 subscribers and route every emission through all of them. Each
   * `expand` iteration completes and is dropped, so the cost is constant however long
   * ingestion takes. Unsubscribing disposes the pending timer and read either way.
   *
   * @param jobId The id the upload's 202 returned.
   */
  follow(jobId: string): Observable<JobPoll> {
    const url = this._apiUrl.build(`documents/upload-status/${ApiUrl.segment(jobId)}`);

    return of<PollStep>({ attempt: 0, poll: null }).pipe(
      expand(({ attempt, poll }) =>
        poll !== null && isTerminal(poll)
          ? EMPTY
          : timer(pollDelayMs(attempt)).pipe(
              switchMap(() => this.read(url)),
              map((next): PollStep => ({ attempt: attempt + 1, poll: next })),
            ),
      ),
      // The seed carries no poll of its own; it exists to start the schedule.
      filter((step): step is PollStep & { poll: JobPoll } => step.poll !== null),
      map(({ poll }) => poll),
    );
  }

  private read(url: string): Observable<JobPoll> {
    return this._http.get<JobStatusDto>(url).pipe(
      map((status): JobPoll => ({ kind: 'status', status })),
      catchError((cause: unknown) => {
        const error = toAppError(cause, { url });

        // The one failure that is not a failure. Everything else — a 500, a dropped
        // connection — is a real error and reaches the caller as one.
        if (error.kind === 'resource-not-found') {
          return of<JobPoll>({ kind: 'expired' });
        }

        throw error;
      }),
    );
  }
}

function isTerminal(poll: JobPoll): boolean {
  return (
    poll.kind === 'expired' ||
    poll.status.state === JOB_STATE.succeeded ||
    poll.status.state === JOB_STATE.failed
  );
}
