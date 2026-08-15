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
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { filter, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ProjectDto } from '@domain/api/project';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

interface ProjectState {
  /** The route's `:projectId`, or null before the route has bound one. */
  readonly projectId: string | null;
  /** The project this screen is showing, once it has arrived. */
  readonly project: ProjectDto | null;
  /** The project was deleted while its own screen was open (US-903). */
  readonly deleted: boolean;
}

/**
 * The open project, from `GET api/projects/{id}` (US-904).
 *
 * The **only** source of `instructions`: the listing projection omits the column, so a
 * card's `ProjectSummaryDto` cannot fill this screen and a detail request is not
 * optional. It is also what every write on this screen echoes back, since
 * `PUT api/projects/{id}` is a full representation.
 *
 * Route-scoped, so a second project's screen starts empty rather than showing the
 * previous one's instructions while its own request flies. `rxMethod` + `switchMap`
 * because navigating between two projects can overlap, and `takeUntil(signedOut$)`
 * because `withResetOnSignOut` clears state and does not cancel work.
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
    /**
     * The header's name, which exists one round trip before the rest of the screen
     * does not. Empty rather than a placeholder: the header renders only once there is
     * a project, and inventing a name would be the fabricated status line US-406
     * refuses.
     */
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
       * Binds the route's `:projectId`, once, from the screen's constructor.
       *
       * A computation rather than a value, so a navigation between two projects
       * re-runs it and `switchMap` cancels the request the reader has left behind.
       */
      bindRoute: _load,

      /** Re-reads the project after a failure. */
      retry(): void {
        _load(store.projectId());
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // A rename, a description edit or an instructions save confirmed by the server.
    // The payload is the DTO the server returned, so this adopts its `dateModified`
    // rather than a local guess — and it is what makes the panel's saved state settle
    // only once the round trip is over (US-904's second criterion).
    events.on(projectEvents.updated).pipe(
      tap(({ payload }) => {
        if (payload.id === store.projectId()) {
          patchState(store, { project: payload });
        }
      }),
    ),

    // Deleted from this screen's own header, or from another tab's grid. The screen
    // navigates away rather than showing a project that is gone; the flag is what the
    // component watches, because a store may not navigate.
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
