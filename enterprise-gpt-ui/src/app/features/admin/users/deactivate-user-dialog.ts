import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  computed,
  inject,
  output,
} from '@angular/core';
import { UserActionsStore } from '@core/users/user-actions-store';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * The deactivate-user confirmation (US-1204).
 *
 * Destructive actions always confirm via modal, naming the person, and red lives only on
 * confirmations like this one. Initial focus lands on **Cancel**, so Enter on a freshly
 * opened confirmation revokes nobody's access.
 *
 * The confirm is emitted rather than called on the store, because only the screen can
 * remove the row and hand back the undo — the directory list is scoped to its route and
 * `core/` may not import from `features/`.
 */
@Component({
  selector: 'app-deactivate-user-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './deactivate-user-dialog.html',
  styleUrl: './deactivate-user-dialog.scss',
})
export class DeactivateUserDialog {
  protected readonly actions = inject(UserActionsStore);
  private readonly document = inject(DOCUMENT);
  private readonly hostRef = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly confirmed = output<void>();

  protected readonly target = this.actions.deactivateTarget;
  protected readonly open = computed(() => this.actions.deactivateTarget() !== null);

  protected readonly name = computed(() => {
    const user = this.target();
    if (user === null) {
      return '';
    }

    return user.fullName.trim() === '' ? user.email : user.fullName;
  });

  /** Set on confirm, so only that close path runs the focus fallback. */
  private confirming = false;

  protected confirm(): void {
    this.confirming = true;
    this.confirmed.emit();
  }

  protected onClosed(): void {
    // A no-op after confirm (the target is already null); syncs the store on Escape and
    // backdrop closes.
    this.actions.cancelDeactivate();

    if (!this.confirming) {
      return;
    }
    this.confirming = false;

    // Confirming removes the row this was invoked from, so the modal's own focus return
    // has nothing to restore to. Checked only when focus actually fell through, so a
    // reader who clicked elsewhere mid-flight is not yanked back.
    const active = this.document.activeElement;
    const fellThrough =
      active === null ||
      active === this.document.body ||
      !active.isConnected ||
      this.hostRef.nativeElement.contains(active);

    if (fellThrough) {
      this.document.getElementById('main-content')?.focus();
    }
  }
}
