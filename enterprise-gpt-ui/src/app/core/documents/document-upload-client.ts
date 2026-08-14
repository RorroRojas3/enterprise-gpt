import { Injectable, inject } from '@angular/core';
import { Observable, Subscriber } from 'rxjs';
import { JobDto, UploadTarget, documentTargetPath } from '@domain/api/document';
import { TokenService } from '@core/auth/token-service';
import { AppError } from '@core/errors/app-error';
import { buildAppError, clientAppError } from '@core/errors/build-app-error';
import { parseProblemDetails } from '@core/errors/problem-details';
import { authErrorDecision } from '@core/errors/retry-policy';
import { toAppError } from '@core/errors/to-app-error';
import { ApiUrl } from '@core/http/api-url';
import { UPLOAD_XHR } from './upload-xhr.token';

/** What the transport reports while one file is on its way to the server. */
export type UploadEvent =
  /** Bytes transferred, 0–100. Emitted only while the length is computable. */
  | { readonly kind: 'progress'; readonly percent: number }
  /** The 202 landed and the job is queued. Terminal. */
  | { readonly kind: 'accepted'; readonly jobId: string };

/** The multipart field name the minimal-API handler binds `IFormFile file` from. */
const FILE_FIELD = 'file';

/**
 * Posts one file to `POST api/documents/{conversations|projects}/{id}`.
 *
 * **Why this is not `HttpClient`.** `HttpEventType.UploadProgress` is emitted by
 * `HttpXhrBackend` only, and this application configures `provideHttpClient(withFetch())`
 * — Angular's fetch backend reports download progress and nothing else. Frame `4l`'s
 * percentage during byte transfer is therefore unobtainable through `HttpClient` here,
 * and a 50 MB file on a slow link would upload with no feedback at all. So the one
 * request that needs upload progress goes over `XMLHttpRequest` directly, exactly as
 * the chat stream goes over raw `fetch` for the same class of reason.
 *
 * Being off the `HttpClient` pipeline means two interceptor behaviours are restated
 * here rather than inherited, and only two:
 *
 * - The bearer token comes from {@link TokenService}, the one source both transports
 *   share, with a **single** forced-refresh replay gated by `authErrorDecision` — the
 *   same predicate `authInterceptor` consults, which is what structurally keeps a 403
 *   `permission-required` out of the refresh path.
 * - `retryInterceptor` is *not* restated, and must not be: it replays idempotent GETs
 *   only, and a replayed upload would queue the file twice.
 *
 * Unsubscribing aborts the request, so a `switchMap` consumer or the chip's remove
 * control cancels the transfer instead of orphaning it.
 */
@Injectable({ providedIn: 'root' })
export class DocumentUploadClient {
  private readonly _apiUrl = inject(ApiUrl);
  private readonly _tokens = inject(TokenService);
  private readonly _newXhr = inject(UPLOAD_XHR);

  /**
   * Uploads one file and completes when the server has queued its job.
   *
   * @param target The conversation or project the document belongs to.
   * @param file The file as the picker or the drop event produced it.
   */
  upload(target: UploadTarget, file: File): Observable<UploadEvent> {
    return new Observable<UploadEvent>((subscriber) => {
      const state: RequestState = { active: null, cancelled: false };

      void this.run(target, file, subscriber, state);

      return () => {
        state.cancelled = true;
        state.active?.abort();
      };
    });
  }

  /** Never rejects: every ending is delivered through the subscriber. */
  private async run(
    target: UploadTarget,
    file: File,
    subscriber: Subscriber<UploadEvent>,
    state: RequestState,
  ): Promise<void> {
    const url = this._apiUrl.build(
      `documents/${documentTargetPath(target)}/${ApiUrl.segment(target.id)}`,
    );

    try {
      let response = await this.issue(url, file, subscriber, state, false);

      if (!isSuccess(response.status)) {
        const error = this.toError(response, url);
        if (authErrorDecision(error) !== 'refresh') {
          subscriber.error(error);

          return;
        }

        response = await this.issue(url, file, subscriber, state, true);
        if (!isSuccess(response.status)) {
          subscriber.error(this.toError(response, url));

          return;
        }
      }

      const jobId = jobIdOf(response.body);
      if (jobId === null) {
        // A 202 with no id means the file is queued and unobservable. Reporting it as
        // a failure is the honest reading: nothing can poll it, so nothing can ever
        // say the document is ready.
        subscriber.error(clientAppError(new Error('The upload was accepted without a job id.')));

        return;
      }

      subscriber.next({ kind: 'accepted', jobId });
      subscriber.complete();
    } catch (thrown) {
      subscriber.error(toAppError(thrown, { url }));
    }
  }

  private issue(
    url: string,
    file: File,
    subscriber: Subscriber<UploadEvent>,
    state: RequestState,
    forceRefresh: boolean,
  ): Promise<XhrResponse> {
    return this._tokens
      .getToken(forceRefresh ? { forceRefresh } : undefined)
      .then((token) => this.send(url, file, token, subscriber, state));
  }

  private send(
    url: string,
    file: File,
    token: string,
    subscriber: Subscriber<UploadEvent>,
    state: RequestState,
  ): Promise<XhrResponse> {
    return new Promise<XhrResponse>((resolve, reject) => {
      if (state.cancelled) {
        reject(abortSignalled());

        return;
      }

      const xhr = this._newXhr();
      state.active = xhr;

      const body = new FormData();
      // The file name is passed explicitly: a `File` reconstructed from a drop event
      // keeps its name, but `FormData.append(field, blob)` without one sends
      // "blob", which the server's 256-character name rule accepts and the user
      // never recognises in the documents list.
      body.append(FILE_FIELD, file, file.name);

      xhr.open('POST', url);
      xhr.responseType = 'json';
      xhr.setRequestHeader('Authorization', `Bearer ${token}`);
      // Content-Type is deliberately not set: the browser has to append the multipart
      // boundary itself, and setting it by hand produces a body no parser can read.

      xhr.upload.addEventListener('progress', (event) => {
        if (event.lengthComputable && event.total > 0) {
          subscriber.next({
            kind: 'progress',
            percent: Math.min(100, Math.round((event.loaded / event.total) * 100)),
          });
        }
      });

      xhr.addEventListener('load', () => {
        state.active = null;
        resolve({ status: xhr.status, body: xhr.response });
      });
      xhr.addEventListener('error', () => {
        state.active = null;
        // A TypeError is what `fetch` reports a transport failure as, and it is what
        // `toAppError` reads to select the network arm — so both transports classify a
        // dropped connection identically.
        reject(new TypeError('The upload could not reach the server.'));
      });
      xhr.addEventListener('abort', () => {
        state.active = null;
        reject(abortSignalled());
      });

      xhr.send(body);
    });
  }

  private toError(response: XhrResponse, url: string): AppError {
    return buildAppError({
      status: response.status,
      problem: parseProblemDetails(response.body),
      url,
      cause: response,
    });
  }
}

interface RequestState {
  active: XMLHttpRequest | null;
  cancelled: boolean;
}

interface XhrResponse {
  readonly status: number;
  readonly body: unknown;
}

function isSuccess(status: number): boolean {
  return status >= 200 && status < 300;
}

function jobIdOf(body: unknown): string | null {
  const id = (body as JobDto | null)?.id;

  return typeof id === 'string' && id !== '' ? id : null;
}

/**
 * `toAppError` selects its aborted arm from the thrown value's `name`, which is how a
 * user's cancel is told apart from a failure without the caller having to say so.
 */
function abortSignalled(): DOMException {
  return new DOMException('The upload was cancelled.', 'AbortError');
}
