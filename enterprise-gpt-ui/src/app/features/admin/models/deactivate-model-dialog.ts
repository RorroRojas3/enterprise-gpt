import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ModelActionsStore } from '@core/catalog/model-actions-store';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * The retire-model confirmation, frame `5e`'s "Delete (confirm)" (US-1207).
 *
 * Destructive actions always confirm via modal, naming the model, and red lives only on
 * confirmations like this one. Initial focus lands on **Cancel**, so Enter on a freshly
 * opened confirmation retires nothing.
 *
 * Unlike `DeactivateUserDialog` this confirms **on the store** rather than emitting.
 * There is nothing for the screen to do: the delete is soft, the row stays in the table
 * and turns muted when the refetch lands, so there is no optimistic removal and no undo
 * to hand back — and therefore no focus fallback either, because the kebab this was
 * invoked from is still in the document for `Modal` to return focus to.
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

  /**
   * Whether retiring this model leaves the deployment with no default at all.
   *
   * `DeactivateModelAsync` clears `IsDefault` in the same statement that sets
   * `DateDeactivated`, and nothing promotes another row — so the warning is a fact about
   * what is about to happen, not a caution.
   */
  protected readonly losesDefault = computed(() => this.target()?.isDefault === true);
}
