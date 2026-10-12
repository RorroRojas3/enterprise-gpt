import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { UsageGroupDto } from '@domain/api/usage-report';
import { formatTokens } from './usage-format';

/** The narrowest bar, so one that rounds to zero still reads as a row with usage. */
const MINIMUM_WIDTH = 2;

/**
 * CSS bars, not SVG: a percentage width reflows with its column for free. Widths are a share of
 * the largest row, not the total, so the tail rows stay visible.
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
      // The floor is for a bar that rounds away; a model that consumed nothing draws no sliver.
      width:
        row.totalTokens <= 0
          ? '0%'
          : `${Math.max(MINIMUM_WIDTH, Math.round((row.totalTokens / peak) * 100))}%`,
      // `--brand` and `--accent` match in the dark theme, so the rank is the second channel
      // that keeps the ordering readable.
      rank: index + 1,
      isLeader: index === 0,
    }));
  });
}
