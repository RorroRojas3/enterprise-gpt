import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { McpServerActionsStore } from '@core/catalog/mcp-server-actions-store';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * The deactivate-server confirmation, frame `5g`'s "Deactivate (confirm)" (US-1208).
 *
 * Destructive actions always confirm via modal, naming the server, and red lives only on
 * confirmations like this one. Initial focus lands on **Cancel**, so Enter on a freshly
 * opened confirmation deactivates nothing.
 *
 * Like `DeactivateModelDialog` and unlike `DeactivateUserDialog`, this confirms **on the
 * store** rather than emitting. There is nothing for the screen to do: the delete is soft,
 * the row stays in the table and turns muted when the refetch lands, so there is no
 * optimistic removal and no undo to hand back — and therefore no focus fallback either,
 * because the kebab it was invoked from is still in the document for `Modal` to return
 * focus to.
 *
 * The body says what the cascade actually does. `DeactivateMcpServerAsync` deactivates the
 * server, its linked permission **and every grant of that permission** in one save, which
 * no other delete in this application does — so a confirmation that mentioned only the
 * server would understate what the reader is about to do.
 */
@Component({
  selector: 'app-deactivate-mcp-server-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './deactivate-mcp-server-dialog.html',
  styleUrl: './deactivate-mcp-server-dialog.scss',
})
export class DeactivateMcpServerDialog {
  protected readonly actions = inject(McpServerActionsStore);

  protected readonly target = this.actions.deactivateTarget;
  protected readonly open = computed(() => this.actions.deactivateTarget() !== null);
}
