import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { IconName } from '@shared/icon/icon-names';
import { Icon } from '@shared/icon/icon';
import { formatDelta } from './usage-format';

/**
 * The delta arrives precomputed, since what counts as "up" differs per tile. Direction is never
 * colour alone (SC 1.4.1), and every rise is `--ok`, cost included: "more spend is bad" is a
 * product opinion this component should not invent.
 */
@Component({
  selector: 'app-kpi-tile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './kpi-tile.html',
  styleUrl: './kpi-tile.scss',
})
export class KpiTile {
  readonly label = input.required<string>();
  readonly value = input.required<string>();

  /** The signed percentage change, or null when the prior period held nothing to compare with. */
  readonly delta = input.required<number | null>();

  /**
   * Named without a preposition ("prior 30 days"): the tile supplies the wording, since its two
   * states need different sentences.
   */
  readonly comparison = input.required<string>();

  protected readonly deltaText = computed(() => formatDelta(this.delta()));

  protected readonly isUp = computed(() => (this.delta() ?? 0) > 0);

  /** No arrow at all for an unchanged figure: a glyph is a channel, and it must not lie. */
  protected readonly isFlat = computed(() => this.delta() === 0);

  protected readonly icon = computed<IconName>(() =>
    this.isUp() ? 'bi-arrow-up-right' : 'bi-arrow-down-right',
  );

  /** False shows a "nothing to compare" line, since a blank would read as a zero change. */
  protected readonly hasDelta = computed(() => this.delta() !== null);
}
