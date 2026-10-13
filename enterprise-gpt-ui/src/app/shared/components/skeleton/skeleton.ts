import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

const RADIUS = { text: 'rounded-2', block: 'rounded-3', circle: 'rounded-circle' } as const;

/**
 * Always `aria-hidden`, since the container swapping it out carries `aria-busy`. Static: a
 * shimmer would add a keyframe the design does not define.
 */
@Component({
  selector: 'app-skeleton',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true', class: 'd-flex flex-column gap-2' },
  templateUrl: './skeleton.html',
})
export class Skeleton {
  readonly variant = input<'text' | 'block' | 'circle'>('text');
  readonly width = input<string>('100%');
  readonly height = input<string>('1em');

  /** Repeats the bar, with the last one short, the way a paragraph reads. */
  readonly lines = input<number>(1);

  protected readonly rows = computed(() => Array.from({ length: Math.max(1, this.lines()) }));
  protected readonly radius = computed(() => RADIUS[this.variant()]);
}
