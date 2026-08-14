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
  /**
   * What the retry affordance does. Required for {@link retryLabel} to render — a
   * Retry with nowhere to go is a button that does nothing when pressed.
   */
  readonly onRetry?: () => void;
  /** Overrides {@link AUTO_DISMISS_MS} for this toast alone. */
  readonly dismissAfterMs?: number;
}

/**
 * The application's transient notifications.
 *
 * It lives in `core/` rather than `shared/` because it is app-wide singleton state
 * written from interceptors, guards and feature stores — `shared/` holds no store, and
 * the HTTP layer that raises an error toast lives in `core/http`. The presentation
 * (`shared/feedback/toast/`) injects it, which is the sanctioned direction.
 */
export const ToastStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withProps(() => ({
    /**
     * Timers live here, not in state. A `Map` in `withState` is either mutated in
     * place — no signal write, so nothing re-renders — or replaced, which re-renders
     * every toast in the region on each schedule and each cancel. Neither is what a
     * timer handle deserves.
     */
    _timers: new Map<string, ReturnType<typeof setTimeout>>(),
    /**
     * Retry handlers, beside the timers and for the same reason: a function is not
     * serializable state, `withResetOnSignOut` would clone-compare it pointlessly,
     * and nothing renders from it.
     */
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
      // Always clear first: a user dismissing a success toast at three seconds would
      // otherwise leave a timer to fire against an id that no longer exists.
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
        // A label with no handler renders nothing: the affordance and the action are
        // one decision, and letting them come apart is how a dead Retry ships.
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

      /**
       * Runs a toast's retry, then dismisses it.
       *
       * Dismissed first so the handler is free to raise a fresh toast for the second
       * attempt without the failed one lingering beneath it.
       *
       * @param id The toast id, as `ToastRegion` reports it.
       */
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
       * The only place an `AppError` becomes a toast.
       *
       * Routing every failure through `shouldNotify`/`userMessage`/`traceLine` is what
       * keeps a toast and the error panel saying the same thing about the same
       * failure, and what keeps a cancelled turn from being announced as an error.
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
      // withResetOnSignOut clears state and nothing else, so the pending auto-dismiss
      // timers have to be cancelled here or one would fire against the next user's
      // session. Toasts carry userMessage(error), detail and traceLine — a previous
      // user's error text is not something to leave on a shared browser.
      injectSignedOut()
        .pipe(takeUntilDestroyed())
        .subscribe(() => {
          for (const timer of store._timers.values()) {
            clearTimeout(timer);
          }
          store._timers.clear();
          // A retry closure captures the previous user's request; it must not survive
          // into the next session any more than the toast it belonged to does.
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
