import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  computed,
  inject,
  output,
} from '@angular/core';
import { Modal } from '@shared/dialog/modal';
import { UserActionsStore } from './user-actions-store';

/**
 * Emits the confirm rather than calling the store, because only the route-scoped screen can
 * remove the row and hand back the undo.
 */
@Component({
  selector: 'app-deactivate-user-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './deactivate-user-dialog.html',
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

    // The invoking row is gone, so the modal's focus return has no target. Only when focus
    // actually fell through, so a reader who clicked elsewhere is not yanked back.
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
