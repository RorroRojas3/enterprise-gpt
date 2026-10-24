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
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { filter, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { ProjectDto } from '@core/api/project.model';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/api/with-request-status';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';

interface ProjectState {
  /** The route's `:projectId`, or null before the route has bound one. */
  readonly projectId: string | null;
  readonly project: ProjectDto | null;
  /** The project was deleted while its own screen was open. */
  readonly deleted: boolean;
}

/**
 * The only source of `instructions`, which the listing omits, and the full representation every
 * write here echoes back. Route-scoped, so another project's screen starts empty;
 * `takeUntil(signedOut$)` because `withResetOnSignOut` clears state but cancels nothing.
 */
export const ProjectStore = signalStore(
  withState<ProjectState>({ projectId: null, project: null, deleted: false }),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _signedOut$: injectSignedOut(),
  })),
  withComputed(({ error, project, isPending }) => ({
    /** Nothing on screen yet, so a skeleton is the honest thing to show. */
    isFirstLoad: computed(() => isPending() && project() === null),
    /** Empty rather than a placeholder: the header renders only once there is a project. */
    name: computed(() => project()?.name ?? ''),
    hasFailed: computed(() => error() !== null),
  })),
  withMethods((store) => {
    const _load = rxMethod<string | null>(
      pipe(
        filter((projectId): projectId is string => projectId !== null),
        tap((projectId) => patchState(store, { projectId, deleted: false }, setPending())),
        switchMap((projectId) => {
          const url = store._api.build(`projects/${ApiUrl.segment(projectId)}`);

          return store._http.get<ProjectDto>(url).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (project) => patchState(store, { project }, setFulfilled()),
              error: (cause: unknown) =>
                patchState(store, { project: null }, setError(toAppError(cause, { url }))),
            }),
          );
        }),
      ),
    );

    return {
      /**
       * Binds a computation, not a value, so moving between projects re-runs it and `switchMap`
       * cancels the request left behind.
       */
      bindRoute: _load,

      /** Re-reads the project after a failure. */
      retry(): void {
        _load(store.projectId());
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // The payload is the server's DTO, so `dateModified` is real and the instructions panel's
    // saved state settles only once the round trip is over.
    events.on(projectEvents.updated).pipe(
      tap(({ payload }) => {
        if (payload.id === store.projectId()) {
          patchState(store, { project: payload });
        }
      }),
    ),

    // One field, because that is all the 204 confirmed: patching a whole DTO would overwrite
    // `instructions` with a value nothing fetched.
    events.on(projectEvents.favorited).pipe(
      tap(({ payload: { id, isFavorite } }) => {
        const current = store.project();
        if (current !== null && current.id === id) {
          patchState(store, { project: { ...current, isFavorite } });
        }
      }),
    ),

    // A flag the component watches, because a store may not navigate.
    events.on(projectEvents.deleted).pipe(
      tap(({ payload: id }) => {
        if (id === store.projectId()) {
          patchState(store, { deleted: true });
        }
      }),
    ),
  ]),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
