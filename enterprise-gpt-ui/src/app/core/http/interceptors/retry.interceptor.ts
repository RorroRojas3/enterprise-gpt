import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { retry, throwError, timer } from 'rxjs';
import { RETRY_POLICY, computeBackoffDelay, isTransientRetriable } from '../../errors/retry-policy';
import { toAppError } from '../../errors/to-app-error';

/**
 * Retries transient failures on idempotent requests with jittered, capped backoff.
 *
 * Restricted to `GET`: no other verb is safe to replay, and the chat stream is a
 * `POST` issued over raw `fetch`, which never reaches an `HttpClient` interceptor
 * at all.
 *
 * Anything not retried is rethrown as the identical {@link HttpErrorResponse}
 * instance, so downstream handlers see the original object.
 *
 * **Keep this first in `withInterceptors`.** When the authentication interceptor
 * lands it must run inside each retry, so a retried request acquires a fresh
 * token rather than replaying the one that already failed.
 */
export const retryInterceptor: HttpInterceptorFn = (request, next) => {
  const policy = inject(RETRY_POLICY);

  if (request.method !== 'GET' || policy.maxRetries <= 0) {
    return next(request);
  }

  return next(request).pipe(
    retry({
      count: policy.maxRetries,
      delay: (error: unknown, retryCount: number) => {
        if (!(error instanceof HttpErrorResponse) || !isTransientRetriable(toAppError(error))) {
          return throwError(() => error);
        }

        return timer(computeBackoffDelay(retryCount - 1, policy));
      },
    }),
  );
};
