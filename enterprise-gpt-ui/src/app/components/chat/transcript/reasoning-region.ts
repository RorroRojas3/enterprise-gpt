import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { Icon } from '@shared/icon/icon';
import { formatDurationSeconds } from './turn-usage';

let nextId = 0;

/**
 * Plain text, not markdown: another `<markdown>` per turn would cost the streaming budget.
 * Expansion is local state, so it lasts the turn and resets on the settled instance.
 */
@Component({
  selector: 'app-reasoning-region',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './reasoning-region.html',
  styleUrl: './reasoning-region.scss',
})
export class ReasoningRegion {
  readonly text = input.required<string>();

  /** Null when nothing measured it, as on a replayed turn. */
  readonly durationSeconds = input<number | null>(null);

  protected readonly expanded = signal(false);

  protected readonly bodyId = `reasoning-body-${nextId++}`;

  protected readonly duration = computed(() =>
    formatDurationSeconds(this.durationSeconds() ?? undefined),
  );

  protected toggle(): void {
    this.expanded.update((expanded) => !expanded);
  }
}
