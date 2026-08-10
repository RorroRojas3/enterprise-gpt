import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The mobile stand-in for a table row — frame `5m`.
 *
 * Pure projection, because every screen's card differs: the admin user card stacks an
 * avatar, a name and an email with wrapped permission chips, while a conversation card
 * is a title and a timestamp. A typed input would have to be the union of all of them.
 *
 * A separate component rather than a media query on {@link DataTable}, because the
 * boards restructure the row rather than reflow it — and a `role="table"` that looks
 * like cards is a lie no stylesheet can fix.
 */
@Component({
  selector: 'app-card-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './card-row.html',
  styleUrl: './card-row.scss',
})
export class CardRow {}
