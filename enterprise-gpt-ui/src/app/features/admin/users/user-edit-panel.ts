import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal } from '@angular/core';
import { PERMISSION_ID } from '@domain/api/permission-ids';
import { UserDto } from '@domain/api/user';
import { unmatchedServerMessages } from '@core/errors/server-messages';
import { SELF_REVOKE_ADMIN_MESSAGE } from '@core/users/self-action-messages';
import { UserActionsStore } from '@core/users/user-actions-store';
import { AvatarInitials } from '@shared/avatar/avatar-initials/avatar-initials';
import { Icon } from '@shared/icon/icon';
import { Offcanvas } from '@shared/overlay/offcanvas/offcanvas';
import { PermissionChecklist } from './permission-checklist';

/**
 * The edit-user drawer, frame `5c` (US-1203).
 *
 * Composes `Offcanvas`, which US-106 built for this frame — 420px, right-anchored, a
 * sticky footer, and focus captured on open and restored to the invoking row's Edit
 * button on close, all of it already in the primitive and already specced.
 *
 * **Not a Signal Form.** The only thing this panel edits is a *set*, and Signal Forms
 * models fields: a checklist would be a field per permission over a model whose keys are
 * server GUIDs, rebuilt every time the catalogue loads. The one message the server can
 * return about it does not belong to a field either — frame `5d` renders it at the
 * Administrator checkbox.
 *
 * The identity block sits at the top of the **body** rather than in the header, because
 * that is where the board puts it and because `Offcanvas` owns its `<h2>`. Frame `5c`'s
 * `· last active yesterday` is absent from it: `UserDto` carries no timestamp of any kind.
 */
@Component({
  selector: 'app-user-edit-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AvatarInitials, Icon, Offcanvas, PermissionChecklist],
  templateUrl: './user-edit-panel.html',
  styleUrl: './user-edit-panel.scss',
})
export class UserEditPanel {
  protected readonly actions = inject(UserActionsStore);

  /**
   * The checked permissions, seeded from the row the panel was opened on.
   *
   * `linkedSignal` rather than an `effect` that writes it: this is writable state derived
   * from a source signal, which is what the primitive is for, and effect-based
   * propagation is the pattern the repo standard names to avoid. Reseeding on the target
   * is what makes a second row's grants replace the first's; a `null` target — the moment
   * after a save closes the panel — keeps what is there rather than blanking the
   * checkboxes while the drawer animates out.
   */
  protected readonly selectedIds = linkedSignal<UserDto | null, ReadonlySet<string>>({
    source: this.actions.formTarget,
    computation: (user, previous) =>
      user === null
        ? (previous?.value ?? new Set<string>())
        : new Set(user.permissions.map(({ id }) => id)),
  });

  protected readonly open = computed(() => this.actions.formMode() === 'edit');
  protected readonly target = this.actions.formTarget;

  protected readonly displayName = computed(() => {
    const user = this.target();
    if (user === null) {
      return '';
    }

    return user.fullName.trim() === '' ? user.email : user.fullName;
  });

  protected readonly canSubmit = computed(() => !this.actions.formBusy());

  /**
   * Everything the server said that is not already rendered at a checkbox.
   *
   * Shown rather than dropped, because a server message the reader cannot see is a save
   * that silently fails. The per-element `PermissionIds[0]` key the request validator
   * uses lands here too, since the checklist has no single row to blame for it.
   *
   * `permissionIds` is filtered out **only** when the self-revocation refusal is being
   * rendered at the Administrator checkbox, so the same sentence is not said twice in the
   * reader's words and the server's.
   */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(
      this.actions.formError(),
      this.actions.formSelfRevokedAdmin() ? ['permissionIds'] : [],
    ),
  );

  /**
   * A field rather than a template arrow: `provideCheckNoChangesConfig({ exhaustive:
   * true })` compares bindings between passes, and a closure allocated in the template is
   * a new value on every check.
   */
  protected readonly messageFor = (id: string): string | null =>
    this.actions.formSelfRevokedAdmin() && id === PERMISSION_ID.administrator
      ? SELF_REVOKE_ADMIN_MESSAGE
      : null;

  protected readonly retryPermissions = (): void => this.actions.retryPermissions();

  protected submit(): void {
    if (!this.canSubmit()) {
      return;
    }

    this.actions.submitEdit([...this.selectedIds()]);
  }
}
