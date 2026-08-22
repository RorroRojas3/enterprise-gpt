import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { IconName } from '@shared/icon/icon-names';
import { Icon } from '@shared/icon/icon';
import { formatDelta } from './usage-format';

/**
 * One of frame `5j`'s four headline figures, with its change against the prior period.
 *
 * The delta is passed in already computed, because what counts as "up" differs per tile and the
 * arithmetic belongs beside the figures rather than inside a presentational component.
 *
 * **Direction is never carried by colour alone** (SC 1.4.1): the arrow glyph and the signed
 * percentage both state it, so the tone is reinforcement rather than the message. The board paints
 * every rise `--ok`, including a rise in cost — kept, because a usage report is read by someone
 * asking what changed, and re-encoding "more spend is bad" here would be this component inventing
 * a product opinion the boards do not hold.
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
   * The period the change is measured against, named without a preposition — "prior 30 days".
   *
   * The tile supplies the wording around it, because the two states need different sentences and
   * a caller passing a whole phrase can only be right for one of them.
   */
  readonly comparison = input.required<string>();

  protected readonly deltaText = computed(() => formatDelta(this.delta()));

  protected readonly isUp = computed(() => (this.delta() ?? 0) > 0);

  /** No arrow at all for an unchanged figure: a glyph is a channel, and it must not lie. */
  protected readonly isFlat = computed(() => this.delta() === 0);

  protected readonly icon = computed<IconName>(() =>
    this.isUp() ? 'bi-arrow-up-right' : 'bi-arrow-down-right',
  );

  /**
   * Whether there is a prior period to compare against at all.
   *
   * False sends the template to a line saying so, rather than leaving the tile blank: a figure
   * with no comparison beside it reads as a comparison that came back zero, which is the one
   * thing it is not.
   */
  protected readonly hasDelta = computed(() => this.delta() !== null);
}
