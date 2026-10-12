import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ModelActionsStore } from '@state/model-actions-store';
import { Modal } from '@shared/components/modal/modal';

/**
 * Initial focus is on Cancel, so Enter retires nothing. Confirms on the store rather than
 * emitting: the soft-deleted row stays in the table, so its kebab is there to take focus back.
 */
@Component({
  selector: 'app-deactivate-model-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './deactivate-model-dialog.html',
  styleUrl: './deactivate-model-dialog.scss',
})
export class DeactivateModelDialog {
  protected readonly actions = inject(ModelActionsStore);

  protected readonly target = this.actions.deactivateTarget;
  protected readonly open = computed(() => this.actions.deactivateTarget() !== null);

  /** Retiring clears `IsDefault` and promotes nothing, so the deployment loses its default. */
  protected readonly losesDefault = computed(() => this.target()?.isDefault === true);
}
