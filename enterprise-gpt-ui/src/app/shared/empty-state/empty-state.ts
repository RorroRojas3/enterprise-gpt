import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Ridgeline } from '@shared/brand/ridgeline';

/**
 * The request succeeded with zero rows, unlike a skeleton or {@link UnavailablePanel}. The
 * action is projected into `[emptyStateAction]`, so the kit never learns callers' labels.
 */
@Component({
  selector: 'app-empty-state',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Ridgeline],
  host: { class: 'd-flex flex-column align-items-center gap-2 mw-sm mx-auto p-4 text-center' },
  templateUrl: './empty-state.html',
})
export class EmptyState {
  readonly heading = input.required<string>();
  readonly message = input<string>('');
  readonly illustration = input<'ridgeline' | 'none'>('ridgeline');

  /** Via `role="heading"` + `aria-level`, so callers set the outline without swapping tags. */
  readonly headingLevel = input<2 | 3 | 4>(3);
}
