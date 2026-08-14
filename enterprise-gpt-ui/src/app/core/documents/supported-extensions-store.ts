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
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import {
  setError,
  setFulfilled,
  setIdle,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

interface SupportedExtensionsState {
  /** Lower-case, leading dot — `['.doc', '.docx', '.md', '.pdf', '.pptx', '.txt']`. */
  readonly extensions: readonly string[];
}

const initialState: SupportedExtensionsState = { extensions: [] };

/**
 * The file types this deployment can actually read, from `GET api/documents/file-extensions`.
 *
 * The server derives the list from its registered extractors rather than from
 * configuration, so it cannot advertise a format nothing can parse — which is why the
 * picker and the client-side refusal both read it instead of hard-coding a set. The
 * design board's own copy is already wrong about this (it names XLSX and CSV, neither
 * supported, and omits PPTX), so nothing in the app quotes the board here.
 *
 * Root-scoped and loaded once per session: it is deployment-shaped, not user-shaped,
 * but a sign-out still clears it because the next user's deployment is only *usually*
 * the same one, and a stale list would be silently wrong rather than visibly so.
 *
 * **A failed load does not block uploading.** {@link isSupported} answers `true` when
 * the list is unknown, so the file goes to the server and is refused there with a real
 * reason. Refusing everything locally because a side request failed would be worse than
 * the round trip it saves.
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
      // Newest-attempt-wins, as the catalog stores do: a retry started while an earlier
      // load is still in flight must not be overwritten by the one it replaced.
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
      /**
       * Loads the list once per session, memoized **only on success**.
       *
       * Unlike the model and MCP catalogs, a failure here is silent: the picker stops
       * narrowing and every client-side refusal disappears, with no error surface of
       * its own to notice it by. Memoizing a failure would make one transient blip
       * last the whole session, so a later composer simply asks again.
       */
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

      /**
       * Whether a file name's extension is one this deployment can read.
       *
       * Answers `true` when the list has not loaded — see the note on the store.
       *
       * @param fileName The name as the user's file system reports it.
       */
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

/**
 * A file name's extension, lower-cased and dotted, or `''` when it has none.
 *
 * A leading-dot file (`.gitignore`) has no extension by this reading, which matches the
 * server: `FileDto.FileExtension` parses the same way and refuses it.
 *
 * @param fileName The name as the user's file system reports it.
 */
export function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf('.');

  return dot > 0 ? fileName.slice(dot).toLowerCase() : '';
}
