import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { PermissionDto } from '@domain/api/user';
import { AppError } from '@core/errors/app-error';
import { Icon } from '@shared/icon/icon';

let nextId = 0;

/** One row of the checklist, with everything the template would otherwise recompute. */
interface ChecklistRow {
  readonly permission: PermissionDto;
  readonly checked: boolean;
  /** The MCP server that owns this permission's definition, or null. */
  readonly managedBy: string | null;
  /** An inline refusal to render beneath this row. */
  readonly message: string | null;
  readonly messageId: string;
}

/**
 * Stateless, so the create modal and edit panel share it without a store. MCP-managed grants
 * are marked, not disabled: the API allows granting them, so disabling would block MCP access.
 */
@Component({
  selector: 'app-permission-checklist',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './permission-checklist.html',
  styleUrl: './permission-checklist.scss',
})
export class PermissionChecklist {
  readonly permissions = input.required<readonly PermissionDto[]>();
  readonly selectedIds = model<ReadonlySet<string>>(new Set<string>());

  readonly loading = input<boolean>(false);
  readonly error = input<AppError | null>(null);
  readonly disabled = input<boolean>(false);

  /** The create and edit forms place the "managed by its MCP server" note differently. */
  readonly captionStyle = input<'inline' | 'block'>('inline');

  /** An inline refusal for one row, which also marks it invalid; no second input is needed. */
  readonly messageFor = input<(id: string) => string | null>(() => null);

  readonly retried = input<(() => void) | null>(null);

  private readonly _id = `checklist-${nextId++}`;

  protected readonly rows = computed<readonly ChecklistRow[]>(() => {
    const selected = this.selectedIds();
    const messageFor = this.messageFor();

    return this.permissions().map((permission) => ({
      permission,
      checked: selected.has(permission.id),
      // A managed permission is named after its server and kept in sync, so no request needed.
      managedBy: permission.mcpServerId === null ? null : permission.name,
      message: messageFor(permission.id),
      messageId: `${this._id}-${permission.id}`,
    }));
  });

  protected toggle(id: string): void {
    // Replaced, never mutated: `model` compares by reference, so an in-place edit writes nothing.
    const next = new Set(this.selectedIds());
    if (!next.delete(id)) {
      next.add(id);
    }

    this.selectedIds.set(next);
  }
}
