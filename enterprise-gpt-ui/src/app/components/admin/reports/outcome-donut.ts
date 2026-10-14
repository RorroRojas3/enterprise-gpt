import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { UsageOutcomeDto } from '@shared/models/api/usage-report.model';
import { formatCount, formatPercent, formatTokens } from '@shared/utils/format/usage-format';
import { StatusDot, StatusTone } from '@shared/components/status-dot/status-dot';

/** A radius-56 ring at stroke width 17 in a 150px box, as the template draws it. */
const RADIUS = 56;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

/** The outcomes in the order the legend lists them, with the tone each arc takes. */
const OUTCOME_TONES: ReadonlyArray<{ status: string; label: string; tone: StatusTone }> = [
  { status: 'Completed', label: 'Completed', tone: 'success' },
  { status: 'Cancelled', label: 'Cancelled', tone: 'secondary' },
  { status: 'Failed', label: 'Failed', tone: 'warning' },
];

/**
 * The arcs are decorative and the legend is the data: colours on a ring fail SC 1.4.1 and expose
 * no geometry, so the ring is `aria-hidden` and every arc is also a legend row.
 */
@Component({
  selector: 'app-outcome-donut',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusDot],
  templateUrl: './outcome-donut.html',
  styleUrl: './outcome-donut.scss',
})
export class OutcomeDonut {
  readonly outcomes = input.required<readonly UsageOutcomeDto[]>();

  protected readonly radius = RADIUS;
  protected readonly circumference = round(CIRCUMFERENCE);

  private readonly _byStatus = computed(() => {
    const seen = new Map(this.outcomes().map((outcome) => [outcome.status, outcome]));

    // Every outcome gets a legend row, since a missing one reads as a category this deployment
    // lacks; only the arcs skip a zero.
    return OUTCOME_TONES.map(({ status, label, tone }) => ({
      status,
      label,
      tone,
      turns: seen.get(status)?.turns ?? 0,
      totalTokens: seen.get(status)?.totalTokens ?? 0,
    }));
  });

  protected readonly totalTurns = computed(() =>
    this._byStatus().reduce((sum, outcome) => sum + outcome.turns, 0),
  );

  protected readonly legend = computed(() =>
    this._byStatus().map((outcome) => ({
      ...outcome,
      figure: `${formatCount(outcome.turns)} · ${formatPercent(outcome.turns, this.totalTurns())}`,
    })),
  );

  /** Each arc's dash pattern and the offset that starts it where the previous one ended. */
  protected readonly arcs = computed(() => {
    const total = this.totalTurns();
    let consumed = 0;

    return (
      this._byStatus()
        // Filtered on the datum, not the formatted dash-array, which would couple it to `round`.
        .filter((outcome) => outcome.turns > 0)
        .map((outcome) => {
          const length = total <= 0 ? 0 : (outcome.turns / total) * CIRCUMFERENCE;
          const arc = {
            status: outcome.status,
            tone: outcome.tone,
            dasharray: `${round(length)} ${round(CIRCUMFERENCE)}`,
            dashoffset: round(-consumed),
          };
          consumed += length;

          return arc;
        })
    );
  });

  /** Cancelled and failed together: from the reader's side both mean the answer never arrived. */
  protected readonly wastedPercent = computed(() => {
    const wasted = this._byStatus()
      .filter((outcome) => outcome.status !== 'Completed')
      .reduce((sum, outcome) => sum + outcome.turns, 0);

    return formatPercent(wasted, this.totalTurns());
  });

  protected readonly wastedTokens = computed(() =>
    formatTokens(
      this._byStatus()
        .filter((outcome) => outcome.status !== 'Completed')
        .reduce((sum, outcome) => sum + outcome.totalTokens, 0),
    ),
  );

  protected readonly hasTurns = computed(() => this.totalTurns() > 0);
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}
