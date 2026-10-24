import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
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
import { firstValueFrom, takeUntil } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import {
  setError,
  setFulfilled,
  setIdle,
  setPending,
  withRequestStatus,
} from '@core/api/with-request-status';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';

interface SupportedExtensionsState {
  /** Lower-case, leading dot — `['.doc', '.docx', '.md', '.pdf', '.pptx', '.txt']`. */
  readonly extensions: readonly string[];
}

const initialState: SupportedExtensionsState = { extensions: [] };

/**
 * The server derives the list from its registered extractors, so never hard-code a set.
 * Cleared on sign-out, since the next user's deployment is only usually the same one.
 */
export const SupportedExtensionsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('documents/file-extensions'),
    _signedOut$: injectSignedOut(),
    _load: { promise: null as Promise<boolean> | null, generation: 0 },
  })),
  withComputed(({ extensions }) => ({
    /** The `accept` attribute for a file input. Empty when the list is unknown. */
    accept: computed(() => extensions().join(',')),
    /** The supported set as the chip names it — `PDF, DOCX, MD, PPTX, TXT`. */
    supportedLabel: computed(() =>
      extensions()
        .map((extension) => extension.replace(/^\./, '').toUpperCase())
        .join(', '),
    ),
  })),
  withMethods((store) => {
    async function load(): Promise<boolean> {
      // Newest attempt wins: a retry must not be overwritten by the load it replaced.
      const generation = ++store._load.generation;
      const isCurrent = (): boolean => generation === store._load.generation;

      patchState(store, setPending());

      try {
        const extensions = await firstValueFrom(
          store._http.get<string[]>(store._url).pipe(takeUntil(store._signedOut$)),
          { defaultValue: null },
        );

        if (!isCurrent()) {
          return false;
        }

        if (extensions === null) {
          patchState(store, setIdle());

          return false;
        }

        patchState(
          store,
          { extensions: extensions.map((extension) => extension.toLowerCase()) },
          setFulfilled(),
        );

        return true;
      } catch (cause) {
        if (!isCurrent()) {
          return false;
        }

        patchState(store, setError(toAppError(cause, { url: store._url })));

        return false;
      }
    }

    return {
      /** Memoized only on success: a failure here is silent, and must not last the session. */
      ensureLoaded(): Promise<boolean> {
        return (store._load.promise ??= load().then((loaded) => {
          if (!loaded) {
            store._load.promise = null;
          }

          return loaded;
        }));
      },

      reload(): Promise<boolean> {
        return (store._load.promise = load());
      },

      /** `true` while the list is unknown, so the server refuses the file with a real reason. */
      isSupported(fileName: string): boolean {
        const known = store.extensions();

        return known.length === 0 || known.includes(extensionOf(fileName));
      },
    };
  }),
  withHooks({
    onInit(store) {
      // The memo is a prop, so `withResetOnSignOut` does not reach it.
      store._signedOut$.pipe(takeUntilDestroyed()).subscribe(() => {
        store._load.promise = null;
      });
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);

/** Lower-cased and dotted, or `''`; `.gitignore` has none, matching `FileDto.FileExtension`. */
export function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf('.');

  return dot > 0 ? fileName.slice(dot).toLowerCase() : '';
}
