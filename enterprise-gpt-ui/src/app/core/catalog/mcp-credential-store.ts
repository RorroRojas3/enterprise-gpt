import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withProps, withState } from '@ngrx/signals';
import { Dispatcher } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { exhaustMap, takeUntil } from 'rxjs';
import { McpCredentialStatusDto, McpDto } from '@domain/api/mcp';
import { AppError } from '@core/errors/app-error';
import { traceLine, userMessage } from '@core/errors/error-message';
import { toAppError } from '@core/errors/to-app-error';
import { mcpEvents } from '@core/events/mcp-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

interface McpCredentialState {
  /** The dialog is open on this server; null keeps it closed. */
  readonly target: McpDto | null;
  /**
   * Which opening of the dialog is on screen, counted from one.
   *
   * A request carries the value it was started under, and a response is only rendered while
   * that value still stands. The server id alone is not enough: closing and reopening the
   * *same* server is a new surface, and an earlier attempt's rejection landing on it would
   * show a message about a key the reader never typed there.
   */
  readonly openId: number;
  /**
   * A save or a removal is in flight. Handed to `Modal.busy`, which suppresses Escape and
   * backdrop dismissal so the reader is not left wondering whether their key was stored.
   */
  readonly busy: boolean;
  /** The last rejection, rendered in the dialog rather than as a toast. */
  readonly error: AppError | null;
  /**
   * The exact key the server rejected. The field shows {@link error}'s messages only while
   * it still holds that value, which is what clears them on the next keystroke — Signal
   * Forms has no `setErrors` for an imperative reset.
   */
  readonly rejectedValue: string | null;
}

const initialState: McpCredentialState = {
  target: null,
  openId: 0,
  busy: false,
  error: null,
  rejectedValue: null,
};

/**
 * The flow behind a tool server that needs an API key of the user's own.
 *
 * Root-scoped for the reason the admin action stores are: `rxMethod` binds its subscription to
 * the injector that created it, and a save started from the composer must survive the user
 * navigating to another conversation while it is in flight.
 *
 * Reached only from the shell's lazy chunk — the composer's Tools menu and the dialog it opens
 * — so `providedIn: 'root'` costs the initial graph nothing. Keep it that way.
 *
 * The boundaries mirror `McpServerActionsStore`: a confirmed change travels as an event rather
 * than as `patchState` into another store, no failure closes the dialog, and **nothing is
 * inferred from what happens to be open** — every response identifies the surface it belongs
 * to.
 */
export const McpCredentialStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _toasts: inject(ToastStore),
    _dispatcher: inject(Dispatcher),
    _signedOut$: injectSignedOut(),
  })),
  withMethods((store) => {
    function url(mcpServerId: string): string {
      return store._api.build(`mcps/${ApiUrl.segment(mcpServerId)}/credential`);
    }

    /** Whether the dialog that started a request is still the one on screen. */
    function isOwnSurface(openId: number): boolean {
      return store.openId() === openId;
    }

    /** Closes the dialog only if the response belongs to it. */
    function resolve(openId: number): void {
      if (isOwnSurface(openId)) {
        patchState(store, { target: null, error: null, rejectedValue: null });
      }
    }

    /**
     * Reports a rejection on the dialog that produced it, and as a toast once that dialog has
     * been replaced — the rule every action store here follows.
     */
    function reject(
      cause: unknown,
      requestUrl: string,
      server: McpDto,
      apiKey: string | null,
      openId: number,
    ): void {
      const error = toAppError(cause, { url: requestUrl });

      if (isOwnSurface(openId)) {
        patchState(store, { error, rejectedValue: apiKey });

        return;
      }

      store._toasts.show({
        tone: 'error',
        title: `Your API key for ${server.name} could not be saved`,
        detail: userMessage(error),
        traceLine: traceLine(error),
      });
    }

    const _save = rxMethod<{ server: McpDto; apiKey: string; openId: number }>(
      // exhaustMap: a second submit while one is in flight is a double-click, not a second key.
      exhaustMap(({ server, apiKey, openId }) => {
        patchState(store, { busy: true, error: null, rejectedValue: null });
        const requestUrl = url(server.id);

        return store._http.put<McpCredentialStatusDto>(requestUrl, { apiKey }).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (status) => {
              store._dispatcher.dispatch(mcpEvents.credentialChanged(status));
              store._toasts.success(`API key saved for ${server.name}`);
              resolve(openId);
            },
            error: (cause: unknown) => reject(cause, requestUrl, server, apiKey, openId),
            // Success, failure and sign-out cancellation alike; the only path that reliably
            // releases the flag, which is why `close()` does not clear it.
            finalize: () => patchState(store, { busy: false }),
          }),
        );
      }),
    );

    const _remove = rxMethod<{ server: McpDto; openId: number }>(
      exhaustMap(({ server, openId }) => {
        patchState(store, { busy: true, error: null, rejectedValue: null });
        const requestUrl = url(server.id);

        return store._http.delete<void>(requestUrl).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              // The 204 carries no body, so the cleared status is built here. It claims nothing
              // the server did not do: the request was an unconditional removal.
              store._dispatcher.dispatch(
                mcpEvents.credentialChanged({
                  mcpServerId: server.id,
                  hasApiKey: false,
                  apiKeyHint: null,
                  dateModified: null,
                }),
              );
              store._toasts.success(`API key removed for ${server.name}`);
              resolve(openId);
            },
            error: (cause: unknown) => reject(cause, requestUrl, server, null, openId),
            finalize: () => patchState(store, { busy: false }),
          }),
        );
      }),
    );

    return {
      open(server: McpDto): void {
        // `busy` survives a force-closed dialog, and `exhaustMap` would drop the next submit
        // anyway: opening now would present a modal whose field and every button are disabled
        // and which Escape cannot dismiss.
        if (store.busy()) {
          return;
        }

        patchState(store, {
          target: server,
          openId: store.openId() + 1,
          error: null,
          rejectedValue: null,
        });
      },

      close(): void {
        // Bumped here too: the surface really is gone, so a response still in flight has
        // nowhere to render and belongs on a toast. Without this the reader force-closes a
        // dialog and is told nothing when the request behind it fails.
        patchState(store, {
          target: null,
          openId: store.openId() + 1,
          error: null,
          rejectedValue: null,
        });
      },

      save(apiKey: string): void {
        const server = store.target();
        if (server === null) {
          return;
        }

        _save({ server, apiKey, openId: store.openId() });
      },

      remove(): void {
        const server = store.target();
        if (server === null || !server.hasUserApiKey) {
          return;
        }

        _remove({ server, openId: store.openId() });
      },
    };
  }),
  withResetOnSignOut(),
);
