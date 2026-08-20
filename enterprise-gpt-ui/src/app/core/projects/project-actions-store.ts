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

/** What the form dialog is doing, which decides its heading and its verb. */
export type ProjectFormMode = 'create' | 'edit';

/** The values the form dialog collects (frame `4h`). */
export interface ProjectFormValue {
  readonly name: string;
  readonly description: string;
  readonly instructions: string;
}

interface ProjectActionsState {
  /** The dialog is open when this is set. `null` on create; a DTO on edit. */
  readonly formMode: ProjectFormMode | null;
  /** The project being edited, or null while creating one — or while fetching it. */
  readonly formTarget: ProjectDto | null;
  /**
   * The edit dialog is open but its project has not arrived yet.
   *
   * Only the listing shape reaches this store from the grid, and it **omits
   * `instructions` entirely** — so opening the form on it and saving would send
   * `instructions: null` on a full-representation PUT and silently wipe the standing
   * instructions US-904 exists to keep. The dialog opens on the click, as the board
   * draws it, and fills in when `GET api/projects/{id}` lands.
   */
  readonly formLoading: boolean;
  /**
   * A create or update is in flight: the dialog blocks dismissal and disables its
   * buttons. In-flight tracking, not dialog state — a dialog force-closed mid-flight
   * (a second Escape is uncancelable in Chromium) leaves this true until the request
   * settles, so a reopened dialog honestly reports that `exhaustMap` is still occupied
   * rather than swallowing the next submit silently.
   */
  readonly formBusy: boolean;
  /** The validation problem the server returned for the last attempt. */
  readonly formError: ValidationAppError | null;
  /**
   * The exact name the server rejected. The dialog shows {@link formError}'s messages
   * only while the field still holds this value, which is what makes the inline error
   * clear itself on the user's next edit without an imperative reset — Signal Forms has
   * no `setErrors` to clear.
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
 * What runs after a project is created, so the invoker gets its answer back.
 *
 * US-903's third criterion: opened from the composer, the modal must return the user
 * *exactly where they were* with the new project selected — never to the Projects
 * screen. A callback rather than an event, because the answer belongs to one caller and
 * `projectEvents.created` is already carrying the fact to everyone else.
 */
type AfterCreate = (project: ProjectDto) => void;

/**
 * The create, rename, describe and delete flows every project surface shares (US-903).
 *
 * Root-scoped for the reason `ConversationActionsStore` is: `rxMethod` binds its
 * subscription to the injector that created it, and a delete navigates away from
 * `/projects/{id}` — a route-scoped store would have the request torn down mid-flight,
 * leaving nothing deleted and nothing said. Today's invokers are the grid's cards and a
 * project's own detail header; US-307 adds the composer's picker without changing a
 * line here.
 *
 * The boundaries are the ones the conversation store documents, and they hold for the
 * same reasons:
 *
 * - **Entity writes travel as events**, never as `patchState` into another store's
 *   protected state. `ProjectListStore` and `ProjectStore` both subscribe.
 * - **Validation problems render inline** in the dialog and are never toasted;
 *   everything else rolls back and goes through `ToastStore.fromError`.
 * - **Events carry server-confirmed facts only.** Creation is not optimistic — it mints
 *   a server id — and the update adopts the DTO the server returned rather than a local
 *   guess at `dateModified`.
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
      // Replaced, never mutated: `patchState` compares by reference, so an in-place
      // `Set` edit performs no signal write at all and the card silently stops updating.
      const next = new Set(store.pendingIds());
      if (pending) {
        next.add(id);
      } else {
        next.delete(id);
      }

      patchState(store, { pendingIds: next });
    }

    /** Renders a rejection inline while the dialog is up, and as a toast when it is not. */
    function rejectForm(cause: unknown, url: string, name: string): void {
      const error = toAppError(cause, { url });

      // Inline only while the dialog is still up: a validation problem landing after
      // the user cancelled has no field to render under, so it degrades to the toast
      // path rather than being dropped.
      if (error.kind === 'validation-error' && store.formMode() !== null) {
        patchState(store, { formError: error, formRejectedName: name });

        return;
      }

      if (error.kind === 'validation-error') {
        // `fromError` silences validation problems on the assumption they render
        // inline; with the dialog force-closed there is no inline, so this one is
        // raised by hand — the same deliberate exception the rename dialog makes.
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
      // exhaustMap: a second submit while one is in flight is a double-click, not a
      // second project. `formBusy` disables the buttons; this closes the race under
      // them — and a duplicate press that slipped through would create two projects
      // whose names then collide with each other.
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
            // Runs on success, failure and sign-out cancellation alike — the one path
            // that reliably releases the flag. It is not cleared in `cancel()`: closing
            // the dialog must not pretend a request still on the wire has finished.
            finalize: () => {
              patchState(store, { formBusy: false });
              store._afterCreate.current = null;
            },
          }),
        );
      }),
    );

    const _update = rxMethod<{ target: ProjectDto; body: ProjectWriteBody }>(
      // exhaustMap, as with create and for the same reason.
      exhaustMap(({ target, body }) => {
        patchState(store, { formBusy: true, formError: null, formRejectedName: null });
        setPending(target.id, true);
        const url = store._api.build(`projects/${ApiUrl.segment(target.id)}`);

        return store._http.put<ProjectDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              // The server's own DTO, with its `dateModified` rather than a local guess
              // — which is what keeps the grid's "Updated …" honest.
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
      // switchMap: a second kebab opened on another card replaces the first, and the
      // response that is still on the wire would otherwise fill the dialog with the
      // project the user just navigated away from.
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
              // The dialog closes rather than offering a form that cannot save
              // correctly: without the project's instructions there is nothing safe to
              // PUT, and a form that looks editable and then clears a field is worse
              // than one that never opened.
              patchState(store, { formMode: null, formTarget: null });
              store._toasts.fromError(toAppError(cause, { url }));
            },
            finalize: () => patchState(store, { formLoading: false }),
          }),
        );
      }),
    );

    const _delete = rxMethod<ProjectSummaryDto>(
      // mergeMap: deletes of different projects are independent and may overlap —
      // exhaustMap would silently drop the second. Same-id overlap cannot happen: the
      // card and its dialog are gone, and `beginDelete` guards on the pending id.
      mergeMap((target) => {
        setPending(target.id, true);
        const url = store._api.build(`projects/${ApiUrl.segment(target.id)}`);

        return store._http.delete<void>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              store._toasts.success(`${target.name} deleted`);
              // Only on the 204. Removal is not optimistic here, unlike a conversation
              // delete: the event also releases every conversation that belonged to the
              // project, and undoing *that* on a failure would mean re-guessing which
              // ones the server had touched.
              store._dispatcher.dispatch(projectEvents.deleted(target.id));
            },
            error: (cause: unknown) => store._toasts.fromError(toAppError(cause, { url })),
            finalize: () => setPending(target.id, false),
          }),
        );
      }),
    );

    const _favorite = rxMethod<{ target: ProjectSummaryDto; isFavorite: boolean }>(
      // mergeMap: favourites of different projects are independent and may overlap.
      // Same-id overlap is closed by the pending-id guard in `toggleFavorite`, which is
      // the whole re-entry story here because nothing opens in front of this one.
      mergeMap(({ target, isFavorite }) => {
        setPending(target.id, true);
        const url = store._api.build(`projects/${ApiUrl.segment(target.id)}/favorite`);

        // A SET, not a toggle: the body carries the state being asked for, so a retried
        // or duplicated request cannot land on the opposite value.
        return store._http.put<void>(url, { isFavorite }).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            // Not optimistic, unlike the conversation star. Entity writes leave this
            // store as events — it has no method channel into `ProjectListStore`, which
            // lives in `features/` — and the only pre-flight event available would be an
            // `updated` carrying a fabricated `ProjectDto`, which `ProjectStore` adopts
            // whole and would blank the standing instructions with. The round trip is
            // covered by the pending flag instead, as every other project write is.
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
      /** Whether one of this store's requests is in flight for a given project. */
      isRowPending(id: string): boolean {
        return store.pendingIds().has(id);
      },

      /**
       * Opens the create dialog (frame `4h`).
       *
       * @param afterCreate Runs once the server confirms, with the project it created.
       *   US-903's third criterion: the composer's invoker uses it to select the new
       *   project and stay exactly where it was.
       */
      beginCreate(afterCreate?: AfterCreate): void {
        store._afterCreate.current = afterCreate ?? null;
        // `formBusy` is deliberately left alone: it tracks the request, not the dialog,
        // and a flight still occupying `exhaustMap` must stay visible.
        patchState(store, {
          formMode: 'create',
          formTarget: null,
          formLoading: false,
          formError: null,
          formRejectedName: null,
        });
      },

      /**
       * Opens the edit dialog, unless the project's own action is in flight.
       *
       * Accepts either shape, and that is the whole point. A caller holding a full
       * `ProjectDto` — the detail screen — opens immediately; the grid holds only
       * `ProjectSummaryDto`, whose JSON **has no `instructions` key at all**, so
       * opening on it would let a save PUT `instructions: null` over text the user
       * never saw. The listing shape therefore costs one `GET api/projects/{id}` first.
       *
       * @param project The project to edit, in either shape.
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

      /**
       * Closes the form dialog. Safe to call from every close path — Cancel, Escape,
       * backdrop — including redundantly after a submit already closed it.
       */
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

      /**
       * Submits the open form.
       *
       * Both verbs go through `toProjectBody`, so neither can drop a field on a
       * full-representation PUT — an edit that sent `{ name }` alone would clear the
       * project's description and its standing instructions.
       *
       * @param value The dialog's fields, already trimmed by it.
       */
      submitForm(value: ProjectFormValue): void {
        const mode = store.formMode();
        if (mode === null || store.formBusy()) {
          return;
        }

        const name = value.name.trim();
        if (name === '') {
          return;
        }

        // Empty is sent as null rather than as `""`: the column is nullable, and a
        // round trip that turns an absent description into an empty string would make
        // "unchanged" look like an edit on the next comparison.
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

      /**
       * Saves standing instructions without opening the dialog (US-904).
       *
       * The same request the edit form issues, built the same way, so the panel cannot
       * drop the name or the description it does not show.
       *
       * @param target The project as the detail screen last saw it.
       * @param instructions The edited text; blank clears the field.
       */
      saveInstructions(target: ProjectDto, instructions: string): void {
        if (store.formBusy() || store.pendingIds().has(target.id)) {
          return;
        }

        _update({
          target,
          body: toProjectBody(target, { instructions: blankToNull(instructions) }),
        });
      },

      /** Opens the delete confirmation, unless the project's own action is in flight. */
      beginDelete(project: ProjectSummaryDto): void {
        if (store.pendingIds().has(project.id)) {
          return;
        }

        patchState(store, { deleteTarget: project });
      },

      /** Closes the delete confirmation. Safe on every close path, confirm included. */
      cancelDelete(): void {
        patchState(store, { deleteTarget: null });
      },

      /**
       * Deletes the open target.
       *
       * The modal closes at once and the request settles behind it: a 204 raises the
       * success toast and announces the deletion, a failure raises an error toast and
       * changes nothing. Nothing is removed optimistically — the same event that drops
       * the card also releases every conversation the project held, and rolling *that*
       * back would mean guessing which rows the server had reached.
       */
      confirmDelete(): void {
        const target = store.deleteTarget();
        if (target === null) {
          return;
        }

        patchState(store, { deleteTarget: null });
        _delete(target);
      },

      /**
       * Stars a project, or clears the star (US-909).
       *
       * Opens nothing, so the pending-id guard *is* the concurrency control: a
       * double-click's second call finds the id the first one set and no-ops, rather
       * than sending an opposite SET that would race the first to decide the final
       * state. It works because `setPending` happens inside the `rxMethod` projection,
       * which runs synchronously with the call.
       *
       * @param target The project as the surface invoking this last saw it.
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
