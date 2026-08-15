import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { filter, mergeMap, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ProjectDocumentDto } from '@domain/api/project';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { addPendingId, removePendingId, withPendingIds } from '@core/state/with-pending-ids';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

/** What the store keeps so an optimistic removal can be put back where it was. */
interface RemovedDocument {
  readonly document: ProjectDocumentDto;
  readonly index: number;
}

interface ProjectDocumentsState {
  /** The project whose documents these are, or null before the route has bound one. */
  readonly projectId: string | null;
  /**
   * The rows, in the server's order.
   *
   * A plain array rather than `withEntities`: `GET api/projects/{id}/documents` answers
   * with a **bare array** and no paging, the list is replaced wholesale on every read,
   * and an optimistic removal has to restore a row at the index it came from — which is
   * bookkeeping an entity map would only get in the way of.
   */
  readonly documents: readonly ProjectDocumentDto[];
}

/**
 * A project's uploaded documents, from `GET api/projects/{id}/documents` (US-905).
 *
 * The route returns every active document at once — no envelope, no `skip`/`take` — so
 * there is no pagination to model and nothing to drain. Route-scoped, because the rows
 * belong to the project on screen.
 *
 * Uploading is **not** here: that is `UploadStore`, which the panel provides alongside
 * this and points at `{ kind: 'project', id }`. The split is the one EP-8 already drew
 * — a file in flight is a chip with a job behind it, a file that finished is a row with
 * a document id — and this store is told to re-read when one becomes the other.
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
      // mergeMap: removals of different documents are independent and may overlap.
      // Same-id overlap cannot happen — the row and its confirmation are gone, and
      // `remove` guards on the pending id.
      mergeMap((document) => {
        const projectId = store.projectId();
        if (projectId === null) {
          return [];
        }

        patchState(store, addPendingId(document.id));

        // Captured before the removal, so a failure can put the row back where it was
        // rather than at either end of the list.
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
              // Only into the list it came out of. A refetch since then would have
              // replaced the array, and splicing a stale row into it would resurrect a
              // document the server has already removed.
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
       * Re-reads the list.
       *
       * Also the panel's Retry, and how a finished upload becomes a row: the upload
       * route answers with a job id rather than a document, so the only way to learn a
       * new document's name, size and created date is to ask for the list again — which
       * is cheap here precisely because it is unpaginated.
       */
      refresh(): void {
        _load(store.projectId());
      },

      /**
       * Removes a document, optimistically, with the row put back on a failure.
       *
       * @param document The row the confirmation named.
       */
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
