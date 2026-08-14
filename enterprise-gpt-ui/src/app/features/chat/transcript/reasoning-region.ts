import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { Icon } from '@shared/icon/icon';
import { formatDurationSeconds } from './turn-usage';

let nextId = 0;

/**
 * The model's reasoning summary, as frame `1f` draws it (US-503): a `--think-bg`
 * pill when collapsed, the same tint as a card when open.
 *
 * The text renders as **plain text**, not markdown. It is a summary the model
 * writes for a reader, not authored content, and putting a third `<markdown>`
 * instance on every turn to parse it would cost the streaming budget US-602
 * exists to protect. `pre-wrap` keeps the paragraph breaks the model streamed,
 * which are the only structure a reasoning summary has.
 *
 * Expansion is local state, which is exactly "persists for the duration of the
 * turn": the live turn's component instance is stable while it streams, and the
 * settled turn is a different instance whose default is collapsed again.
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

  /**
   * Measured while the turn streamed, and null when nothing measured it — a
   * replayed turn, whose reasoning the transcript never stored.
   */
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
