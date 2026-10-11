import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { McpServerActionsStore } from '@core/catalog/mcp-server-actions-store';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * Confirms on the store rather than emitting: the delete is soft and the row stays, so there is
 * no optimistic removal, undo or focus fallback for the screen to handle.
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
