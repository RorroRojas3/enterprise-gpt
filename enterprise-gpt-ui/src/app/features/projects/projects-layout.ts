import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { DeleteProjectDialog } from './dialogs/delete-project-dialog';
import { ProjectFormDialog } from './dialogs/project-form-dialog';

/**
 * The parent of every projects screen, and the one place its dialogs mount.
 *
 * It exists for the reason `Shell` mounts the conversation dialogs: the create, rename
 * and delete flows are invoked from both the grid's cards and a project's own detail
 * header, and one instance driven by `ProjectActionsStore` beats two that could both be
 * open. A native `<dialog>` renders in the top layer, so their position here carries no
 * visual meaning.
 *
 * They are not in `Shell` itself, even though that is where the conversation dialogs
 * live, because every invoker is still inside this feature — US-307 adds the first one
 * outside it (the composer's project picker), and that is the story that should move
 * them and pay the shell-chunk cost.
 */
@Component({
  selector: 'app-projects-layout',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DeleteProjectDialog, ProjectFormDialog, RouterOutlet],
  template: `
    <router-outlet />
    <app-project-form-dialog />
    <app-delete-project-dialog />
  `,
})
export class ProjectsLayout {
  constructor() {
    const actions = inject(ProjectActionsStore);

    // The store is root-scoped and these dialogs are not: `Modal` closes its native
    // element on destroy *without* emitting `closed`, so a reader who leaves
    // `/projects` with the create dialog open would leave `formMode` set in root state
    // — and coming back would re-mount the dialog already open, holding the previous
    // attempt. The conversation dialogs never hit this because `Shell` is not unmounted
    // while signed in.
    inject(DestroyRef).onDestroy(() => {
      // Not while a create is on the wire: `cancelForm` also drops the `afterCreate`
      // continuation, and the 201 would land with the invoker that asked for it never
      // told. No caller passes one today; US-307's composer picker is the story that
      // will, and it invokes this dialog from outside this layout.
      if (!actions.formBusy()) {
        actions.cancelForm();
      }
      actions.cancelDelete();
    });
  }
}
