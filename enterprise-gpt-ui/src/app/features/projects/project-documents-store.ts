import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { tapResponse } from '@ngrx/operators';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { filter, mergeMap, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { ProjectDocumentDto } from '@core/api/project.model';
import { addPendingId, removePendingId, withPendingIds } from '@core/api/with-pending-ids';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/api/with-request-status';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';

/** What the store keeps so an optimistic removal can be put back where it was. */
interface RemovedDocument {
  readonly document: ProjectDocumentDto;
  readonly index: number;
}

interface ProjectDocumentsState {
  /** The project whose documents these are, or null before the route has bound one. */
  readonly projectId: string | null;
  /**
   * A plain array, not `withEntities`: the route answers with a bare, unpaged array, and a failed
   * removal restores its row at the original index.
   */
  readonly documents: readonly ProjectDocumentDto[];
}

/**
 * Uploads live in `UploadStore`: a file in flight is a chip there, a landed one a row here, and
 * this store is told to re-read when one becomes the other.
 */
export const ProjectDocumentsStore = signalStore(
  withState<ProjectDocumentsState>({ projectId: null, documents: [] }),
  withRequestStatus(),
  withPendingIds(),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _toasts: inject(ToastStore),
    _signedOut$: injectSignedOut(),
  })),
  withComputed(({ documents, isFulfilled, isPending }) => ({
    isEmpty: computed(() => isFulfilled() && documents().length === 0),
    /** Nothing on screen yet: a skeleton, not an empty state. */
    isFirstLoad: computed(() => isPending() && documents().length === 0),
    count: computed(() => documents().length),
  })),
  withMethods((store) => {
    function documentsUrl(projectId: string): string {
      return store._api.build(`projects/${ApiUrl.segment(projectId)}/documents`);
    }

    const _load = rxMethod<string | null>(
      pipe(
        filter((projectId): projectId is string => projectId !== null),
        tap((projectId) => patchState(store, { projectId }, setPending())),
        // switchMap: navigating to another project must not let the previous one's
        // response land in this list.
        switchMap((projectId) =>
          store._http.get<readonly ProjectDocumentDto[]>(documentsUrl(projectId)).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (documents) => patchState(store, { documents }, setFulfilled()),
              error: (cause: unknown) =>
                patchState(store, setError(toAppError(cause, { url: documentsUrl(projectId) }))),
            }),
          ),
        ),
      ),
    );

    const _remove = rxMethod<ProjectDocumentDto>(
      // mergeMap: removals of different documents may overlap; `remove` guards same-id ones.
      mergeMap((document) => {
        const projectId = store.projectId();
        if (projectId === null) {
          return [];
        }

        patchState(store, addPendingId(document.id));

        // Captured first, so a failure puts the row back where it was.
        const removed: RemovedDocument = {
          document,
          index: store.documents().findIndex((candidate) => candidate.id === document.id),
        };
        patchState(store, {
          documents: store.documents().filter((candidate) => candidate.id !== document.id),
        });

        const url = store._api.build(
          `projects/${ApiUrl.segment(projectId)}/documents/${ApiUrl.segment(document.id)}`,
        );

        return store._http.delete<void>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => store._toasts.success(`${document.name} removed`),
            error: (cause: unknown) => {
              // Only into the list it came out of: a refetch since would have replaced the
              // array, and splicing a stale row into it could resurrect a removed document.
              if (store.projectId() === projectId) {
                const next = [...store.documents()];
                next.splice(Math.min(Math.max(removed.index, 0), next.length), 0, removed.document);
                patchState(store, { documents: next });
              }

              store._toasts.fromError(toAppError(cause, { url }));
            },
            finalize: () => patchState(store, removePendingId(document.id)),
          }),
        );
      }),
    );

    return {
      /** Binds the route's `:projectId`, once, from the panel's constructor. */
      bindProject: _load,

      /**
       * Also the panel's Retry, and how a finished upload becomes a row: the upload route answers
       * with a job id, not the document.
       */
      refresh(): void {
        _load(store.projectId());
      },

      /** Optimistic; the row is put back on failure. */
      remove(document: ProjectDocumentDto): void {
        if (store.isRowPending(document.id)) {
          return;
        }

        _remove(document);
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
