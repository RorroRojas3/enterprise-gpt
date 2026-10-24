import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { EMPTY, Observable, catchError, expand, filter, map, of, switchMap, timer } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { JOB_STATE, JobStatusDto } from '@core/api/document.model';
import { toAppError } from '@core/errors/to-app-error';
import { pollDelayMs } from './upload-schedule';

/** What one poll of an upload job produced. */
export type JobPoll =
  | { readonly kind: 'status'; readonly status: JobStatusDto }
  /** A 404: expired, evicted, or someone else's job, which the server makes indistinguishable. */
  | { readonly kind: 'expired' };

/** One step of the schedule: how many polls have run, and what the last one said. */
interface PollStep {
  readonly attempt: number;
  readonly poll: JobPoll | null;
}

/**
 * Emits every poll on `pollDelayMs`'s schedule and completes on the first terminal one; only a
 * 404 is not an error. Nothing retries: `retryInterceptor` already replays 502/503/504 GETs.
 */
@Injectable({ providedIn: 'root' })
export class UploadStatusClient {
  private readonly _http = inject(HttpClient);
  private readonly _apiUrl = inject(ApiUrl);

  /**
   * `expand`, not a recursive `concat`: each level would stay subscribed, so a long job would
   * retain hundreds of subscribers. Each `expand` iteration is dropped, keeping the cost constant.
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

        // The one failure that is not a failure; a 500 or a dropped connection still errors.
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
