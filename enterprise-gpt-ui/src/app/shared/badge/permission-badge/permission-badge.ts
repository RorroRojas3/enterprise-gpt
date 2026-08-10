import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Icon } from '@shared/icon/icon';

/**
 * A permission grant, in the admin user list and detail drawer.
 *
 * `managedBy` marks a grant the administrator cannot revoke here — one that comes from
 * an MCP server, or a self-grant the server refuses to remove. The lock glyph is
 * decoration; the reason is spelled out for assistive technology, because "there is a
 * padlock" is not a reason.
 */
@Component({
  selector: 'app-permission-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './permission-badge.html',
  styleUrl: './permission-badge.scss',
})
export class PermissionBadge {
  readonly name = input.required<string>();
  readonly managedBy = input<string | null>(null);
}
