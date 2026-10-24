import { computed } from '@angular/core';
import { signalStoreFeature, withComputed, withState } from '@ngrx/signals';
import { AppError } from '@core/errors/app-error';

/** One field rather than `isLoading` + `error`, which can be loading and errored at once. */
export type RequestStatus = 'idle' | 'pending' | 'fulfilled' | { readonly error: AppError };

export interface RequestStatusState {
  readonly requestStatus: RequestStatus;
}

/** The error arm keeps the whole {@link AppError}, since error surfaces show its `traceId`. */
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
