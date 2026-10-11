import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Ridgeline } from '@shared/ridgeline/ridgeline';

/**
 * The request succeeded with zero rows, unlike a skeleton or {@link UnavailablePanel}. The
 * action is projected into `[emptyStateAction]`, so the kit never learns callers' labels.
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

  /** Via `role="heading"` + `aria-level`, so callers set the outline without swapping tags. */
  readonly headingLevel = input<2 | 3 | 4>(3);
}
