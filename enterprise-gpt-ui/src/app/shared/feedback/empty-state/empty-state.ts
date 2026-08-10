import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Ridgeline } from '@shared/ridgeline/ridgeline';

/**
 * A surface with nothing in it — no conversations yet, no search results, no files.
 *
 * Distinct from a loading skeleton and from {@link UnavailablePanel}: this one means
 * the request succeeded and the answer was zero rows.
 *
 * The action is projected into `[emptyStateAction]`, so the kit never learns about
 * "New conversation" or "Clear filter".
 */
@Component({
  selector: 'app-empty-state',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Ridgeline],
  templateUrl: './empty-state.html',
  styleUrl: './empty-state.scss',
})
export class EmptyState {
  readonly heading = input.required<string>();
  readonly message = input<string>('');
  readonly illustration = input<'ridgeline' | 'none'>('ridgeline');

  /**
   * Rendered through `role="heading"` + `aria-level` rather than a real `<h2>`–`<h4>`,
   * so the caller controls the document outline without the kit swapping tag names.
   */
  readonly headingLevel = input<2 | 3 | 4>(3);
}
