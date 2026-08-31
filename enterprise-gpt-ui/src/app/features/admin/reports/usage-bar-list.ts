import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { UsageGroupDto } from '@domain/api/usage-report';
import { formatTokens } from './usage-format';

/** The narrowest bar the board will draw, so a rounding to zero still reads as a row with usage. */
const MINIMUM_WIDTH = 2;

/**
 * Frame `5j`'s "Tokens by model — top 10": labelled horizontal bars, as CSS rather than SVG.
 *
 * The board draws these as nested `div`s and so does this, which is not a shortcut: a bar whose
 * width is a percentage reflows with its column for free, where an SVG would need a viewBox and a
 * resize observer to do the same thing.
 *
 * **Bar width is a share of the largest row, not of the total.** Ten models summing to 100% would
 * make the leader's bar a quarter of the track and every tail row invisible; against the peak, the
 * leader fills the track and the rest are read against it, which is the comparison the panel is
 * for.
 */
@Component({
  selector: 'app-usage-bar-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-bar-list.html',
  styleUrl: './usage-bar-list.scss',
})
export class UsageBarList {
  readonly rows = input.required<readonly UsageGroupDto[]>();

  protected readonly bars = computed(() => {
    const rows = this.rows();
    const peak = Math.max(1, ...rows.map((row) => row.totalTokens));

    return rows.map((row, index) => ({
      key: row.key,
      label: row.label,
      value: formatTokens(row.totalTokens),
      // The floor applies to a bar that rounds away, not to one that is genuinely zero: a model
      // that consumed nothing must not draw the same sliver as one that consumed a little.
      width:
        row.totalTokens <= 0
          ? '0%'
          : `${Math.max(MINIMUM_WIDTH, Math.round((row.totalTokens / peak) * 100))}%`,
      // The board tints the leader `--brand` and everything else `--accent`. In the dark theme
      // those two tokens are the same colour, so the highlight would vanish exactly where a
      // reader is least able to spot a subtle one — the rank number beside the label is the
      // second channel that keeps the ordering readable in both themes without inventing
      // a token.
      rank: index + 1,
      isLeader: index === 0,
    }));
  });
}
