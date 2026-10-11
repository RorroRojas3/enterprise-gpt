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
 * Not a Signal Form: it edits one set, which as fields would be one per server GUID, rebuilt on
 * every catalog load. Its one server message belongs to the Administrator checkbox, not a field.
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
   * Reseeded per target, so a second row's grants replace the first's. A `null` target (a save
   * closing the panel) keeps the checkboxes rather than blanking them mid-animation.
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
   * Server messages not shown at a checkbox, so none silently fails the save. `permissionIds` is
   * excluded only while the self-revocation refusal shows at the Administrator row.
   */
  protected readonly unmatched = computed(() =>
    unmatchedServerMessages(
      this.actions.formError(),
      this.actions.formSelfRevokedAdmin() ? ['permissionIds'] : [],
    ),
  );

  /** A field, not a template arrow: exhaustive no-changes checks see a new closure each pass. */
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
