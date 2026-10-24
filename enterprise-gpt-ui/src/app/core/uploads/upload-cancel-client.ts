import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { EMPTY, catchError, takeUntil } from 'rxjs';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url-service';

/**
 * Root-scoped so the request outlives the screen: a cancel often precedes a navigation. Silent,
 * since the server removes whatever it produced and there is nothing for the reader to act on.
 */
@Injectable({ providedIn: 'root' })
export class UploadCancelClient {
  private readonly _http = inject(HttpClient);
  private readonly _apiUrl = inject(ApiUrl);
  private readonly _signedOut$ = injectSignedOut();

  cancel(jobId: string): void {
    const url = this._apiUrl.build(`documents/upload-status/${ApiUrl.segment(jobId)}`);

    this._http
      .delete<void>(url)
      .pipe(
        takeUntil(this._signedOut$),
        // A 404 means the job is already gone; anything else is cleanup nobody is watching.
        catchError(() => EMPTY),
      )
      .subscribe();
  }
}
