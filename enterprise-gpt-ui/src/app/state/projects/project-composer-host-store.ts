import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { Router } from '@angular/router';
import { tapResponse } from '@ngrx/operators';
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
import { Subject, exhaustMap, merge, takeUntil } from 'rxjs';
import { ConversationDto } from '@shared/models/api/conversation.model';
import {
  ComposerExportTarget,
  ComposerHost,
  ComposerProjectTarget,
} from '@core/chat/composer-host.token';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url-service';
import { CHAT_ROUTE, PROJECTS_ROUTE } from '@core/navigation/route-paths';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { COMPOSER_UPLOADS } from '@state/documents/composer-uploads.token';
import { PendingAttachmentsStore } from '@state/pending-attachments-store';
import { PendingPromptStore } from '@state/pending-prompt-store';
import { ProjectStore } from './project-store';

interface ProjectComposerState {
  /** `POST api/conversations` is in flight for a prompt typed on this screen. */
  readonly creating: boolean;
  /** A one-shot text handed back to the composer when a send could not proceed. */
  readonly composerSeed: { readonly text: string } | null;
}

/**
 * Does not stream: it creates the conversation, hands the prompt to `PendingPromptStore` and
 * navigates, so `TurnStore` runs the turn on `/chat`. `inFlight` is the create, `stop()` cancels
 * that POST, and failures are toasts because there is no transcript here.
 */
export const ProjectComposerHost = signalStore(
  withState<ProjectComposerState>({ creating: false, composerSeed: null }),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _router: inject(Router),
    _project: inject(ProjectStore),
    _pending: inject(PendingPromptStore),
    _attachments: inject(PendingAttachmentsStore),
    _uploads: inject(COMPOSER_UPLOADS),
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
    /** Always null: failures here are toasts, and null keeps the composer's focus fixups intact. */
    turnError: computed<object | null>(() => null),
    /** Always null: this screen has no conversation to export. */
    exportTarget: computed<ComposerExportTarget | null>(() => null),
    /**
     * The `project` arm renders a static chip rather than a picker: a conversation created here is
     * inside this project by definition.
     */
    projectTarget: computed<ComposerProjectTarget | null>(() => {
      const projectId = _project.projectId();

      return projectId === null ? null : { kind: 'project', projectId };
    }),
  })),
  withMethods((store) => {
    const _create = rxMethod<string>(
      // exhaustMap: a second press during the POST is a double-click, not a second conversation.
      exhaustMap((prompt) => {
        patchState(store, { creating: true });
        store._inFlightPrompt.current = prompt;
        const url = store._api.build('conversations');
        const projectId = store._project.projectId();

        return store._http.post<ConversationDto>(url, { projectId }).pipe(
          // The POST has no abort surface, so Stop and sign-out cancel it here, before `next`.
          takeUntil(merge(store._signedOut$, store._stop$)),
          tapResponse({
            next: (created) => {
              // Before navigating, so the sidebar row exists when the chat route renders.
              store._list.prependNewest(created);
              // Read at the 201, so a chip removed mid-create stays behind. Both holds precede
              // the navigation, which mounts the screen that claims them.
              const files = store._uploads.pendingFiles();
              if (files.length > 0) {
                store._attachments.hold(created.id, files);
              }
              store._pending.hold(created.id, prompt);
              void store._router.navigate([CHAT_ROUTE, created.id]);
            },
            error: (cause: unknown) => {
              const error = toAppError(cause, { url });

              // A 404 means the project was deleted elsewhere, so this URL now names nothing.
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

              // The text goes back to the composer rather than being lost.
              patchState(store, { composerSeed: { text: prompt } });
              store._toasts.fromError(error);
            },
            finalize: () => patchState(store, { creating: false }),
          }),
        );
      }),
    );

    return {
      /** Creates a conversation inside this project and sends the prompt on the chat route. */
      send(prompt: string): void {
        const text = prompt.trim();
        if (text === '' || store.creating() || store._project.projectId() === null) {
          return;
        }

        _create(text);
      },

      /** `finalize` clears `creating` on cancellation, so this only hands the text back. */
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
