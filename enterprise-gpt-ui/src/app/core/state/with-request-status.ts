import { computed } from '@angular/core';
import { signalStoreFeature, withComputed, withState } from '@ngrx/signals';
import { AppError } from '../errors/app-error';

/**
 * The lifecycle of one request, as a single value.
 *
 * Deliberately one field rather than the `isLoading` + `error` pair it replaces:
 * that pair can represent "loading and errored at the same time", which is not a
 * state any screen should have to render, and which is where stale spinners come
 * from.
 */
export type RequestStatus = 'idle' | 'pending' | 'fulfilled' | { readonly error: AppError };

export interface RequestStatusState {
  readonly requestStatus: RequestStatus;
}

/**
 * Adds `requestStatus` and its derived flags to a store.
 *
 * The error arm carries the whole {@link AppError} rather than a message string,
 * because the error surfaces in the design — toasts, the error panel — show the
 * `traceId`, and re-deriving one from a string is impossible.
 */
export function withRequestStatus() {
  return signalStoreFeature(
    withState<RequestStatusState>({ requestStatus: 'idle' }),
    withComputed(({ requestStatus }) => ({
      isIdle: computed(() => requestStatus() === 'idle'),
      isPending: computed(() => requestStatus() === 'pending'),
      isFulfilled: computed(() => requestStatus() === 'fulfilled'),
      error: computed(() => {
        const status = requestStatus();
        return typeof status === 'object' ? status.error : null;
      }),
    })),
  );
}

export function setPending(): RequestStatusState {
  return { requestStatus: 'pending' };
}

export function setFulfilled(): RequestStatusState {
  return { requestStatus: 'fulfilled' };
}

export function setError(error: AppError): RequestStatusState {
  return { requestStatus: { error } };
}

export function setIdle(): RequestStatusState {
  return { requestStatus: 'idle' };
}
