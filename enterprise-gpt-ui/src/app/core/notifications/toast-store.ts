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
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';
import {
  AUTO_DISMISS_MS,
  MAX_PERSISTENT_TOASTS,
  Toast,
  ToastTone,
  isPersistent,
} from './toast.model';

interface ToastState {
  readonly toasts: readonly Toast[];
}

const initialState: ToastState = { toasts: [] };

/** A toast's one button. The toast dismisses itself before `run` is called. */
export interface ToastAction {
  readonly label: string;
  readonly run: () => void;
}

export interface ToastInput {
  readonly tone: ToastTone;
  readonly title: string;
  readonly detail?: string | null;
  readonly traceLine?: string | null;
  readonly retryLabel?: string | null;
  /** Required for {@link retryLabel} to render. */
  readonly onRetry?: () => void;
  /** Wins over {@link onRetry}: a toast has one button. */
  readonly action?: ToastAction;
  /** A live toast with the same key is replaced, so a repeated notice never stacks. */
  readonly key?: string;
}

/** In `core/`: core stores and services inject it, and `core/` may not import a feature. */
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
    _actions: new Map<string, () => void>(),
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
      store._actions.delete(id);
      patchState(store, ({ toasts }) => ({ toasts: toasts.filter((toast) => toast.id !== id) }));
    }

    function dismissKey(key: string): void {
      const live = store.toasts().find((toast) => toast.key === key);
      if (live !== undefined) {
        dismiss(live.id);
      }
    }

    /** Action toasts go first: the sidebar mark or upload chip still carries what they said. */
    function evictOverCap(): void {
      const persistent = store.toasts().filter((toast) => toast.persistent);
      if (persistent.length < MAX_PERSISTENT_TOASTS) {
        return;
      }

      const oldest = persistent.find((toast) => toast.actionLabel !== null) ?? persistent[0];
      if (oldest !== undefined) {
        dismiss(oldest.id);
      }
    }

    function show(input: ToastInput): string {
      const id = `toast-${store._sequence.value++}`;
      // A label with no handler renders nothing, so a dead Retry cannot ship.
      const action: ToastAction | null =
        input.action ??
        (input.onRetry ? { label: input.retryLabel ?? 'Retry', run: input.onRetry } : null);

      const toast: Toast = {
        id,
        tone: input.tone,
        title: input.title,
        detail: input.detail ?? null,
        traceLine: input.traceLine ?? null,
        actionLabel: action?.label ?? null,
        key: input.key ?? null,
        persistent: isPersistent(input.tone, action !== null),
      };

      if (toast.key !== null) {
        dismissKey(toast.key);
      }
      if (action !== null) {
        store._actions.set(id, action.run);
      }

      if (toast.persistent) {
        evictOverCap();
      } else {
        store._timers.set(
          id,
          setTimeout(() => dismiss(id), AUTO_DISMISS_MS),
        );
      }

      patchState(store, ({ toasts }) => ({ toasts: [...toasts, toast] }));

      return id;
    }

    return {
      show,
      dismiss,
      dismissKey,

      /** Dismisses first, so a toast the handler raises does not stack under the failed one. */
      runAction(id: string): void {
        const handler = store._actions.get(id);
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

        const title = userMessage(error);

        return show({
          tone: 'error',
          title,
          traceLine: traceLine(error),
          onRetry,
          // Keyed so an outage replaces rather than stacks; a Retry's closure must not be lost.
          key: onRetry === undefined ? `error:${title}` : undefined,
        });
      },

      dismissAll(): void {
        for (const timer of store._timers.values()) {
          clearTimeout(timer);
        }
        store._timers.clear();
        store._actions.clear();
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
          // An action closure captures the previous user's request.
          store._actions.clear();
        });
    },
    onDestroy(store) {
      for (const timer of store._timers.values()) {
        clearTimeout(timer);
      }
      store._timers.clear();
      store._actions.clear();
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
