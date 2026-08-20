import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { Router } from '@angular/router';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { Subject, exhaustMap, merge, takeUntil } from 'rxjs';
import { ConversationDto } from '@domain/api/conversation';
import { CHAT_ROUTE, PROJECTS_ROUTE } from '@core/auth/auth-routes';
import {
  ComposerExportTarget,
  ComposerHost,
  ComposerProjectTarget,
} from '@core/chat/composer-host';
import { PendingPromptStore } from '@core/chat/pending-prompt-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { ProjectStore } from './project-store';

interface ProjectComposerState {
  /** `POST api/conversations` is in flight for a prompt typed on this screen. */
  readonly creating: boolean;
  /** A one-shot text handed back to the composer when a send could not proceed. */
  readonly composerSeed: { readonly text: string } | null;
}

/**
 * What the composer sends into on a project's screen (US-906, frame `4e`).
 *
 * `COMPOSER_HOST` for this route, where `TurnStore` is the one on `/chat`. The
 * difference the token exists for: **this host does not stream.** The criterion is that
 * the turn streams *as it does on the chat route*, and the transcript lives there — so
 * a prompt sent here creates the conversation inside the project, hands the text to
 * `PendingPromptStore`, and navigates. `TurnStore` picks it up on the route binding and
 * runs the ordinary turn, which is how the whole streaming, activity-timeline and
 * markdown path is reused rather than reproduced.
 *
 * Three consequences, all deliberate:
 *
 * - **`inFlight` is the create**, not a turn. It is what turns Send into Stop for the
 *   one round trip this screen owns.
 * - **`stop()` cancels that POST.** There is nothing else to stop, and the prompt goes
 *   back to the composer rather than being lost.
 * - **`turnError` is always null.** A failure here is a toast, because there is no
 *   transcript on this screen for a notice card to sit in.
 */
export const ProjectComposerHost = signalStore(
  withState<ProjectComposerState>({ creating: false, composerSeed: null }),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _router: inject(Router),
    _project: inject(ProjectStore),
    _pending: inject(PendingPromptStore),
    _list: inject(ConversationListStore),
    _toasts: inject(ToastStore),
    _signedOut$: injectSignedOut(),
    /** Cancels the create when the user presses Stop. */
    _stop$: new Subject<void>(),
    /** The prompt the in-flight create carries, so Stop can hand it back. */
    _inFlightPrompt: { current: '' },
  })),
  withComputed(({ creating, _project }) => ({
    inFlight: computed(() => creating()),
    /**
     * Never a notice. The chat route puts a failed turn in the transcript with a Retry
     * beside it; this screen has no transcript, so its failures are toasts and this
     * stays null so the composer's focus fixups behave exactly as they do there.
     */
    turnError: computed<object | null>(() => null),
    /**
     * Never a download. This screen has no conversation — a prompt sent here creates one
     * and navigates — so there is no transcript for the control to act on, and frame `2f`
     * draws none here (US-1502).
     */
    exportTarget: computed<ComposerExportTarget | null>(() => null),
    /**
     * The route's own project (US-307). The `project` arm makes the composer render a
     * static chip rather than a picker: a conversation created here is created *inside*
     * this project by definition, and there is nothing yet to move.
     */
    projectTarget: computed<ComposerProjectTarget | null>(() => {
      const projectId = _project.projectId();

      return projectId === null ? null : { kind: 'project', projectId };
    }),
  })),
  withMethods((store) => {
    const _create = rxMethod<string>(
      // exhaustMap: a second press while the POST is out is a double-click, not a
      // second conversation — and two would both be created inside the project.
      exhaustMap((prompt) => {
        patchState(store, { creating: true });
        store._inFlightPrompt.current = prompt;
        const url = store._api.build('conversations');
        const projectId = store._project.projectId();

        return store._http.post<ConversationDto>(url, { projectId }).pipe(
          // The POST has no abort surface of its own, so Stop and sign-out cancel it
          // here — before the `next` that would navigate. Unsubscribing is what makes
          // Stop mean stopped rather than "carry on, but pretend otherwise".
          takeUntil(merge(store._signedOut$, store._stop$)),
          tapResponse({
            next: (created) => {
              // Before navigating, so the sidebar row is already there when the chat
              // route renders — the same ordering US-401's own create uses.
              store._list.prependNewest(created);
              store._pending.hold(created.id, prompt);
              void store._router.navigate([CHAT_ROUTE, created.id]);
            },
            error: (cause: unknown) => {
              const error = toAppError(cause, { url });

              // A 404 on create means the project was deleted somewhere else — the
              // server reports a foreign or deactivated project exactly as it reports
              // one that never existed. The reader is told, and returned to the grid,
              // because this URL now names nothing.
              if (error.kind === 'resource-not-found') {
                store._toasts.show({
                  tone: 'error',
                  title: 'This project no longer exists',
                  detail: 'It was deleted, so the conversation could not be created.',
                  traceLine: null,
                });
                void store._router.navigate([PROJECTS_ROUTE]);

                return;
              }

              // The text goes back to the composer rather than being lost, which is the
              // same bargain `TurnStore` makes when a create is stopped.
              patchState(store, { composerSeed: { text: prompt } });
              store._toasts.fromError(error);
            },
            finalize: () => patchState(store, { creating: false }),
          }),
        );
      }),
    );

    return {
      /**
       * Creates a conversation inside this project and sends the prompt on the chat
       * route.
       *
       * @param prompt The text the composer holds.
       */
      send(prompt: string): void {
        const text = prompt.trim();
        if (text === '' || store.creating() || store._project.projectId() === null) {
          return;
        }

        _create(text);
      },

      /**
       * Cancels the create, handing the prompt back to the composer.
       *
       * `finalize` clears `creating` on the cancellation, so the send control returns
       * on its own; this only has to restore the text. Nothing has to be unwound —
       * the conversation was never created, so there is no row and no navigation.
       */
      stop(): void {
        if (!store.creating()) {
          return;
        }

        store._stop$.next();
        patchState(store, { composerSeed: { text: store._inFlightPrompt.current } });
      },

      /** The composer acknowledges a seed it has applied. */
      consumeComposerSeed(): void {
        patchState(store, { composerSeed: null });
      },
    };
  }),
  withHooks({
    onDestroy(store) {
      store._stop$.complete();
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);

// Compile-time proof that this store satisfies the token's contract, which
// `useExisting` alone would not check.
export type ProjectComposerHostSatisfiesContract =
  InstanceType<typeof ProjectComposerHost> extends ComposerHost ? true : never;
