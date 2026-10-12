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
 * Uses `XMLHttpRequest` because the fetch backend never reports upload progress. Restates the
 * auth interceptor's single forced-refresh replay, but never the retry: a replayed upload would
 * queue the file twice. Unsubscribing aborts the request.
 */
@Injectable({ providedIn: 'root' })
export class DocumentUploadClient {
  private readonly _apiUrl = inject(ApiUrl);
  private readonly _tokens = inject(TokenService);
  private readonly _newXhr = inject(UPLOAD_XHR);

  /** Completes once the server has queued the job. */
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
        // A 202 with no id is unobservable: nothing can poll it, so it is a failure.
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
      // Named explicitly: `FormData.append` without a name can send "blob", which the
      // server accepts and the user never recognises.
      body.append(FILE_FIELD, file, file.name);

      xhr.open('POST', url);
      xhr.responseType = 'json';
      xhr.setRequestHeader('Authorization', `Bearer ${token}`);
      // No Content-Type: the browser must append the multipart boundary itself.

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
        // A TypeError, as `fetch` throws, so `toAppError` classifies a dropped connection the
        // same way for both transports.
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

/** `toAppError` reads the `AbortError` name to tell a cancel from a failure. */
function abortSignalled(): DOMException {
  return new DOMException('The upload was cancelled.', 'AbortError');
}
