import { computed } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { AppError } from '@core/errors/app-error';
import { shouldNotify, traceLine, userMessage } from '@core/errors/error-message';
import { injectSignedOut } from '@core/events/session-events';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { AUTO_DISMISS_MS, Toast, ToastTone } from './toast.model';

interface ToastState {
  readonly toasts: readonly Toast[];
}

const initialState: ToastState = { toasts: [] };

export interface ToastInput {
  readonly tone: ToastTone;
  readonly title: string;
  readonly detail?: string | null;
  readonly traceLine?: string | null;
  readonly retryLabel?: string | null;
  /** Required for {@link retryLabel} to render. */
  readonly onRetry?: () => void;
  /** Overrides {@link AUTO_DISMISS_MS} for this toast alone. */
  readonly dismissAfterMs?: number;
}

/** In `core/`: interceptors, guards and feature stores write it, and `shared/` holds no store. */
export const ToastStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withProps(() => ({
    /**
     * Not in state: a `Map` there is either mutated without notifying or replaced, re-rendering
     * every toast on each schedule and cancel.
     */
    _timers: new Map<string, ReturnType<typeof setTimeout>>(),
    /** Beside the timers: a function is not state, and nothing renders from it. */
    _retries: new Map<string, () => void>(),
    _sequence: { value: 0 },
  })),
  withComputed(({ toasts }) => ({
    /** Success and info: announced politely, so they do not interrupt. */
    politeToasts: computed(() =>
      toasts().filter((toast) => toast.tone === 'success' || toast.tone === 'info'),
    ),
    /** Errors and warnings: announced assertively, because they need acting on. */
    assertiveToasts: computed(() =>
      toasts().filter((toast) => toast.tone === 'error' || toast.tone === 'warning'),
    ),
  })),
  withMethods((store) => {
    function dismiss(id: string): void {
      // A manual dismissal would otherwise leave the timer to fire against a removed id.
      const timer = store._timers.get(id);
      if (timer !== undefined) {
        clearTimeout(timer);
        store._timers.delete(id);
      }
      store._retries.delete(id);
      patchState(store, ({ toasts }) => ({ toasts: toasts.filter((toast) => toast.id !== id) }));
    }

    function show(input: ToastInput): string {
      const id = `toast-${store._sequence.value++}`;
      const dismissAfterMs = input.dismissAfterMs ?? AUTO_DISMISS_MS[input.tone];

      const toast: Toast = {
        id,
        tone: input.tone,
        title: input.title,
        detail: input.detail ?? null,
        traceLine: input.traceLine ?? null,
        // A label with no handler renders nothing, so a dead Retry cannot ship.
        retryLabel: input.onRetry ? (input.retryLabel ?? 'Retry') : null,
        dismissAfterMs,
      };

      if (input.onRetry) {
        store._retries.set(id, input.onRetry);
      }

      patchState(store, ({ toasts }) => ({ toasts: [...toasts, toast] }));

      if (dismissAfterMs > 0) {
        store._timers.set(
          id,
          setTimeout(() => dismiss(id), dismissAfterMs),
        );
      }

      return id;
    }

    return {
      show,
      dismiss,

      /** Dismisses first, so a toast the handler raises does not stack under the failed one. */
      retry(id: string): void {
        const handler = store._retries.get(id);
        dismiss(id);
        handler?.();
      },

      success(title: string, detail?: string): string {
        return show({ tone: 'success', title, detail });
      },

      info(title: string, detail?: string): string {
        return show({ tone: 'info', title, detail });
      },

      warning(title: string, detail?: string): string {
        return show({ tone: 'warning', title, detail });
      },

      /**
       * The only place an `AppError` becomes a toast, so a toast and the error panel always agree.
       *
       * @returns The toast id, or null when the error is deliberately silent.
       */
      fromError(error: AppError, onRetry?: () => void): string | null {
        if (!shouldNotify(error)) {
          return null;
        }
        return show({
          tone: 'error',
          title: userMessage(error),
          traceLine: traceLine(error),
          onRetry,
        });
      },

      dismissAll(): void {
        for (const timer of store._timers.values()) {
          clearTimeout(timer);
        }
        store._timers.clear();
        store._retries.clear();
        patchState(store, initialState);
      },
    };
  }),
  withHooks({
    onInit(store) {
      // withResetOnSignOut clears state only, so a pending timer would fire in the next
      // user's session.
      injectSignedOut()
        .pipe(takeUntilDestroyed())
        .subscribe(() => {
          for (const timer of store._timers.values()) {
            clearTimeout(timer);
          }
          store._timers.clear();
          // A retry closure captures the previous user's request.
          store._retries.clear();
        });
    },
    onDestroy(store) {
      for (const timer of store._timers.values()) {
        clearTimeout(timer);
      }
      store._timers.clear();
      store._retries.clear();
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
