import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { EMPTY, catchError, takeUntil } from 'rxjs';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';

/**
 * Calls a running upload off.
 *
 * A plain service rather than a store: nothing here is rendered, so there is no state to hold. Root
 * scoped so the request outlives the screen that issued it — a cancel is very often the last thing
 * that happens before a navigation tears the composer down.
 *
 * Fire and forget, and silent. The server stops the ingestion and removes whatever it had already
 * produced, so there is no outcome the reader could act on; a toast about it would report work they
 * cannot influence.
 */
@Injectable({ providedIn: 'root' })
export class UploadCancelClient {
  private readonly _http = inject(HttpClient);
  private readonly _apiUrl = inject(ApiUrl);
  private readonly _signedOut$ = injectSignedOut();

  /**
   * Cancels the job behind an upload, and removes anything it managed to create.
   *
   * @param jobId The id the upload's 202 returned.
   */
  cancel(jobId: string): void {
    const url = this._apiUrl.build(`documents/upload-status/${ApiUrl.segment(jobId)}`);

    this._http
      .delete<void>(url)
      .pipe(
        takeUntil(this._signedOut$),
        // A 404 means the job is already gone, and anything else is a cleanup the reader never
        // asked to watch. Either way there is nothing to say.
        catchError(() => EMPTY),
      )
      .subscribe();
  }
}
