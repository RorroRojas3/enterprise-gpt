import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The mobile stand-in for a table row. Pure projection, since every screen's card differs; not a
 * {@link DataTable} media query, since a `role="table"` styled as cards misreports structure.
 */
@Component({
  selector: 'app-card-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './card-row.html',
  styleUrl: './card-row.scss',
})
export class CardRow {}
