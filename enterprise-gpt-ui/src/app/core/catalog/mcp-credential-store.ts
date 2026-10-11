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
   * Which opening of the dialog is on screen. A response renders only under the id it started
   * with, since reopening the same server is a new surface.
   */
  readonly openId: number;
  /** A save or removal is in flight; `Modal.busy` blocks dismissal until the outcome is known. */
  readonly busy: boolean;
  /** The last rejection, rendered in the dialog rather than as a toast. */
  readonly error: AppError | null;
  /**
   * The key the server rejected. {@link error} shows only while the field still holds it, so it
   * clears on the next keystroke; Signal Forms has no `setErrors` to reset it.
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
 * Root-scoped so a save survives navigating away mid-flight; reach it only from the shell's lazy
 * chunk to keep it out of the initial graph. Each response carries the surface it belongs to.
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

    function isOwnSurface(openId: number): boolean {
      return store.openId() === openId;
    }

    function resolve(openId: number): void {
      if (isOwnSurface(openId)) {
        patchState(store, { target: null, error: null, rejectedValue: null });
      }
    }

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
            // The only path that always releases `busy`, so `close()` leaves it alone.
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
              // The 204 has no body; an unconditional removal makes the cleared status safe.
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
        // `busy` survives a force-closed dialog; opening now would show a disabled modal that
        // Escape cannot dismiss.
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
        // Bumped so a response still in flight reports on a toast, not in a dialog that's gone.
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
