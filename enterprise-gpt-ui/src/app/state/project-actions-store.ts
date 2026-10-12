import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withProps, withState } from '@ngrx/signals';
import { Dispatcher } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { exhaustMap, mergeMap, switchMap, takeUntil } from 'rxjs';
import { ProjectDto, ProjectSummaryDto, ProjectWriteBody } from '@domain/api/project';
import { ValidationAppError } from '@core/errors/app-error';
import { traceLine } from '@core/errors/error-message';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { projectNameMessages } from './duplicate-name';
import { ProjectChanges, toProjectBody } from './project-update';

export type ProjectFormMode = 'create' | 'edit';

export interface ProjectFormValue {
  readonly name: string;
  readonly description: string;
  readonly instructions: string;
}

interface ProjectActionsState {
  /** The dialog is open when this is set. */
  readonly formMode: ProjectFormMode | null;
  /** The project being edited, or null while creating one — or while fetching it. */
  readonly formTarget: ProjectDto | null;
  /**
   * The edit dialog is open, awaiting `GET api/projects/{id}`: the grid's listing shape omits
   * `instructions`, and saving without them would wipe them on the full PUT.
   */
  readonly formLoading: boolean;
  /**
   * A create or update is in flight. Outlives a force-closed dialog, so a reopened one shows that
   * `exhaustMap` is still occupied instead of silently swallowing the next submit.
   */
  readonly formBusy: boolean;
  /** The validation problem the server returned for the last attempt. */
  readonly formError: ValidationAppError | null;
  /**
   * The name the server rejected; {@link formError} shows only while the field still holds it,
   * since Signal Forms has no `setErrors`.
   */
  readonly formRejectedName: string | null;
  /** The project the delete confirmation names. Null keeps it closed. */
  readonly deleteTarget: ProjectSummaryDto | null;
  /** Ids whose own request is in flight, so one card's kebab disables and not forty. */
  readonly pendingIds: ReadonlySet<string>;
}

const initialState: ProjectActionsState = {
  formMode: null,
  formTarget: null,
  formLoading: false,
  formBusy: false,
  formError: null,
  formRejectedName: null,
  deleteTarget: null,
  pendingIds: new Set<string>(),
};

/**
 * Returns a create's result to its invoker, so the composer can select the new project in place.
 * A callback, not an event: the answer belongs to one caller.
 */
type AfterCreate = (project: ProjectDto) => void;

/**
 * Root-scoped so a delete survives navigating away from `/projects/{id}`. Validation renders
 * inline; other failures toast. Events carry only server-confirmed facts.
 */
export const ProjectActionsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _toasts: inject(ToastStore),
    _dispatcher: inject(Dispatcher),
    _signedOut$: injectSignedOut(),
    /** Set for the flight of one create; see {@link AfterCreate}. */
    _afterCreate: { current: null as AfterCreate | null },
  })),
  withMethods((store) => {
    function setPending(id: string, pending: boolean): void {
      // Replaced, never mutated: an in-place `Set` edit keeps the reference, so nothing updates.
      const next = new Set(store.pendingIds());
      if (pending) {
        next.add(id);
      } else {
        next.delete(id);
      }

      patchState(store, { pendingIds: next });
    }

    function rejectForm(cause: unknown, url: string, name: string): void {
      const error = toAppError(cause, { url });

      // After a cancel there is no field to render under, so it falls through to a toast.
      if (error.kind === 'validation-error' && store.formMode() !== null) {
        patchState(store, { formError: error, formRejectedName: name });

        return;
      }

      if (error.kind === 'validation-error') {
        // `fromError` silences validation errors, assuming they render inline; here they can't.
        store._toasts.show({
          tone: 'error',
          title: 'The project could not be saved',
          detail: projectNameMessages(error).join(' ') || error.message,
          traceLine: traceLine(error),
        });
      } else {
        store._toasts.fromError(error);
      }

      patchState(store, { formMode: null, formTarget: null });
    }

    const _create = rxMethod<ProjectWriteBody>(
      // exhaustMap: a second submit in flight is a double-click, not a second project. Closes
      // the race under `formBusy`.
      exhaustMap((body) => {
        patchState(store, { formBusy: true, formError: null, formRejectedName: null });
        const url = store._api.build('projects');

        return store._http.post<ProjectDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (created) => {
              store._dispatcher.dispatch(projectEvents.created(created));
              store._toasts.success(`${created.name} created`);
              patchState(store, { formMode: null, formTarget: null });
              // After the dispatch, so a caller that navigates finds the lists already
              // holding the row it is navigating to.
              store._afterCreate.current?.(created);
            },
            error: (cause: unknown) => rejectForm(cause, url, body.name),
            // The only path that always releases the flag, so `cancelForm()` leaves it alone.
            finalize: () => {
              patchState(store, { formBusy: false });
              store._afterCreate.current = null;
            },
          }),
        );
      }),
    );

    const _update = rxMethod<{ target: ProjectDto; body: ProjectWriteBody }>(
      exhaustMap(({ target, body }) => {
        patchState(store, { formBusy: true, formError: null, formRejectedName: null });
        setPending(target.id, true);
        const url = store._api.build(`projects/${ApiUrl.segment(target.id)}`);

        return store._http.put<ProjectDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              // The server's DTO, so the grid's "Updated …" shows its `dateModified`, not a guess.
              store._dispatcher.dispatch(projectEvents.updated(updated));
              patchState(store, { formMode: null, formTarget: null });
            },
            error: (cause: unknown) => rejectForm(cause, url, body.name),
            finalize: () => {
              setPending(target.id, false);
              patchState(store, { formBusy: false });
            },
          }),
        );
      }),
    );

    const _loadForEdit = rxMethod<ProjectSummaryDto>(
      // switchMap: opening another card's kebab must not let the first response fill the dialog.
      switchMap((summary) => {
        patchState(store, {
          formMode: 'edit',
          formTarget: null,
          formLoading: true,
          formError: null,
          formRejectedName: null,
        });
        const url = store._api.build(`projects/${ApiUrl.segment(summary.id)}`);

        return store._http.get<ProjectDto>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (project) => patchState(store, { formTarget: project }),
            error: (cause: unknown) => {
              // Close rather than offer a form whose save would clear the instructions it
              // never loaded.
              patchState(store, { formMode: null, formTarget: null });
              store._toasts.fromError(toAppError(cause, { url }));
            },
            finalize: () => patchState(store, { formLoading: false }),
          }),
        );
      }),
    );

    const _delete = rxMethod<ProjectSummaryDto>(
      // mergeMap: deletes of different projects may overlap; `beginDelete`'s pending-id guard
      // rules out the same one twice.
      mergeMap((target) => {
        setPending(target.id, true);
        const url = store._api.build(`projects/${ApiUrl.segment(target.id)}`);

        return store._http.delete<void>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              store._toasts.success(`${target.name} deleted`);
              // Not optimistic: the event also releases the project's conversations, and undoing
              // that would mean guessing which ones the server touched.
              store._dispatcher.dispatch(projectEvents.deleted(target.id));
            },
            error: (cause: unknown) => store._toasts.fromError(toAppError(cause, { url })),
            finalize: () => setPending(target.id, false),
          }),
        );
      }),
    );

    const _favorite = rxMethod<{ target: ProjectSummaryDto; isFavorite: boolean }>(
      // mergeMap across projects; `toggleFavorite`'s pending-id guard closes same-id overlap.
      mergeMap(({ target, isFavorite }) => {
        setPending(target.id, true);
        const url = store._api.build(`projects/${ApiUrl.segment(target.id)}/favorite`);

        // A SET, not a toggle: the body carries the state being asked for, so a retried
        // or duplicated request cannot land on the opposite value.
        return store._http.put<void>(url, { isFavorite }).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            // Not optimistic: the only pre-flight event would be an `updated` with a fabricated
            // DTO, which `ProjectStore` adopts whole, blanking the instructions.
            next: () =>
              store._dispatcher.dispatch(projectEvents.favorited({ id: target.id, isFavorite })),
            // No validation arm: the endpoint has no validator, so a 400 here is a
            // binding failure with no field to render under.
            error: (cause: unknown) => store._toasts.fromError(toAppError(cause, { url })),
            finalize: () => setPending(target.id, false),
          }),
        );
      }),
    );

    return {
      isRowPending(id: string): boolean {
        return store.pendingIds().has(id);
      },

      beginCreate(afterCreate?: AfterCreate): void {
        store._afterCreate.current = afterCreate ?? null;
        // `formBusy` tracks the request, not the dialog, so it is left alone.
        patchState(store, {
          formMode: 'create',
          formTarget: null,
          formLoading: false,
          formError: null,
          formRejectedName: null,
        });
      },

      /**
       * A `ProjectSummaryDto` has no `instructions` key, so opening on it costs a fetch first;
       * otherwise a save would PUT `instructions: null` over text the user never saw.
       */
      beginEdit(project: ProjectDto | ProjectSummaryDto): void {
        if (store.pendingIds().has(project.id)) {
          return;
        }

        store._afterCreate.current = null;

        // The key, not its value: `instructions` is legitimately null on a project
        // without any, and the listing omits the property rather than nulling it.
        if (!('instructions' in project)) {
          _loadForEdit(project);

          return;
        }

        patchState(store, {
          formMode: 'edit',
          formTarget: project,
          formLoading: false,
          formError: null,
          formRejectedName: null,
        });
      },

      /** Safe on every close path, including after a submit already closed the dialog. */
      cancelForm(): void {
        store._afterCreate.current = null;
        patchState(store, {
          formMode: null,
          formTarget: null,
          formLoading: false,
          formError: null,
          formRejectedName: null,
        });
      },

      submitForm(value: ProjectFormValue): void {
        const mode = store.formMode();
        if (mode === null || store.formBusy()) {
          return;
        }

        const name = value.name.trim();
        if (name === '') {
          return;
        }

        // Null, not `""`: an empty string would make "unchanged" look like an edit next time.
        const changes: ProjectChanges = {
          name,
          description: blankToNull(value.description),
          instructions: blankToNull(value.instructions),
        };

        if (mode === 'create') {
          _create({
            name,
            description: changes.description ?? null,
            instructions: changes.instructions ?? null,
          });

          return;
        }

        const target = store.formTarget();
        if (target === null) {
          return;
        }

        _update({ target, body: toProjectBody(target, changes) });
      },

      /** Saves instructions without the dialog; the body still echoes the name and description. */
      saveInstructions(target: ProjectDto, instructions: string): void {
        if (store.formBusy() || store.pendingIds().has(target.id)) {
          return;
        }

        _update({
          target,
          body: toProjectBody(target, { instructions: blankToNull(instructions) }),
        });
      },

      beginDelete(project: ProjectSummaryDto): void {
        if (store.pendingIds().has(project.id)) {
          return;
        }

        patchState(store, { deleteTarget: project });
      },

      /** Safe on every close path, confirm included. */
      cancelDelete(): void {
        patchState(store, { deleteTarget: null });
      },

      confirmDelete(): void {
        const target = store.deleteTarget();
        if (target === null) {
          return;
        }

        patchState(store, { deleteTarget: null });
        _delete(target);
      },

      /**
       * The pending-id guard is the concurrency control: `setPending` runs synchronously in the
       * `rxMethod` projection, so a double-click's second call no-ops instead of racing.
       */
      toggleFavorite(target: ProjectSummaryDto): void {
        if (store.pendingIds().has(target.id)) {
          return;
        }

        _favorite({ target, isFavorite: !target.isFavorite });
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);

/** An untouched optional field is absent, not empty — the column is nullable. */
function blankToNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed === '' ? null : trimmed;
}
