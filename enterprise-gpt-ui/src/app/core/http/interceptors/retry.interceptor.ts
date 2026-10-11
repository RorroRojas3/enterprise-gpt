import {
  HttpContext,
  HttpContextToken,
  HttpErrorResponse,
  HttpInterceptorFn,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { retry, throwError, timer } from 'rxjs';
import { RETRY_POLICY, computeBackoffDelay, isTransientRetriable } from '../../errors/retry-policy';
import { toAppError } from '../../errors/to-app-error';

/**
 * For non-JSON responses, whose `Blob` error body cannot be classified: an application 503 that
 * must never be retried would look like a gateway 503.
 */
const SKIP_RETRY = new HttpContextToken<boolean>(() => false);

export function skipRetry(context = new HttpContext()): HttpContext {
  return context.set(SKIP_RETRY, true);
}

/**
 * Retries transient `GET` failures only, since no other verb is safe to replay. Keep it first in
 * `withInterceptors` so `authInterceptor` runs inside each retry and acquires a fresh token.
 */
export const retryInterceptor: HttpInterceptorFn = (request, next) => {
  const policy = inject(RETRY_POLICY);

  if (request.method !== 'GET' || policy.maxRetries <= 0 || request.context.get(SKIP_RETRY)) {
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
