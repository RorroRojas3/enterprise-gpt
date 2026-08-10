import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * A loading placeholder.
 *
 * Always `aria-hidden`: the container that swaps it for content carries `aria-busy`,
 * and a screen reader reading out a row of grey boxes helps nobody.
 *
 * Static, with no shimmer. Bootstrap's `_placeholders.scss` would add a fifth keyframe
 * against the four the design defines, and a static placeholder is also what the
 * reduced-motion path would collapse to anyway.
 */
@Component({
  selector: 'app-skeleton',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true', class: 'skeleton-host' },
  templateUrl: './skeleton.html',
  styleUrl: './skeleton.scss',
})
export class Skeleton {
  readonly variant = input<'text' | 'block' | 'circle'>('text');
  readonly width = input<string>('100%');
  readonly height = input<string>('1em');

  /** Repeats the bar, with the last one short, the way a paragraph reads. */
  readonly lines = input<number>(1);

  protected readonly rows = computed(() => Array.from({ length: Math.max(1, this.lines()) }));
}
