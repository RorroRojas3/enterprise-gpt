import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Icon } from '@shared/components/icon/icon';

/**
 * `managedBy` marks a grant that cannot be revoked here; the reason is spelled out for assistive
 * technology, since the padlock is decoration.
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
