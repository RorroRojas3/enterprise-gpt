import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Modal } from '@shared/dialog/modal';
import { McpServerActionsStore } from './mcp-server-actions-store';

/**
 * Confirms on the store rather than emitting: the delete is soft and the row stays, so there is
 * no optimistic removal or undo, and `AdminMcps` recovers focus from its kebab.
 */
@Component({
  selector: 'app-deactivate-mcp-server-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './deactivate-mcp-server-dialog.html',
})
export class DeactivateMcpServerDialog {
  protected readonly actions = inject(McpServerActionsStore);

  protected readonly target = this.actions.deactivateTarget;
  protected readonly open = computed(() => this.actions.deactivateTarget() !== null);
}
