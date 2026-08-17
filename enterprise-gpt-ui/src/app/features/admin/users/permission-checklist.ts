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
  /** An inline refusal to render beneath this row — frame `5d`'s first state. */
  readonly message: string | null;
  readonly messageId: string;
}

/**
 * The permission checkboxes both user forms carry — frame `5b`'s list and frame `5c`'s.
 *
 * Dumb by construction: it takes the permissions and the checked set and emits the next
 * one, so the create modal and the edit panel share the markup without sharing a store.
 *
 * **A grant whose definition belongs to an MCP server is marked, not disabled.** The API
 * permits granting and revoking one — `EnsurePermissionIsCustom` guards only edits to the
 * permission *itself* — the boards draw them checked and interactive, and disabling them
 * would leave no way to give anybody MCP tool access at all. The lock says the row's
 * definition is the server's, not that the grant is frozen.
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

  /**
   * Where the "managed by its MCP server" note goes.
   *
   * Frame `5b` puts it inline after the label; frame `5c` puts it on its own line under
   * one, with a lock glyph. The two boards genuinely differ, so this is the difference
   * rather than a preference.
   */
  readonly captionStyle = input<'inline' | 'block'>('inline');

  /**
   * An inline refusal for one row — frame `5d`'s Administrator checkbox.
   *
   * A row that has one is also marked invalid; the two are the same fact, so there is no
   * second input to keep in step with this one.
   */
  readonly messageFor = input<(id: string) => string | null>(() => null);

  readonly retried = input<(() => void) | null>(null);

  private readonly _id = `checklist-${nextId++}`;

  protected readonly rows = computed<readonly ChecklistRow[]>(() => {
    const selected = this.selectedIds();
    const messageFor = this.messageFor();

    return this.permissions().map((permission) => ({
      permission,
      checked: selected.has(permission.id),
      // The server's name for the permission *is* the server's own name:
      // `McpServerService` creates it that way and re-syncs it on rename, and the two
      // share one uniqueness space. So the caption needs no second request to fill.
      managedBy: permission.mcpServerId === null ? null : permission.name,
      message: messageFor(permission.id),
      messageId: `${this._id}-${permission.id}`,
    }));
  });

  protected toggle(id: string): void {
    // Replaced, never mutated: a `model` compares by reference, so an in-place edit
    // performs no write at all and the checkbox silently stops responding.
    const next = new Set(this.selectedIds());
    if (!next.delete(id)) {
      next.add(id);
    }

    this.selectedIds.set(next);
  }
}
