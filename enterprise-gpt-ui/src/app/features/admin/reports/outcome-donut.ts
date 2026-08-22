import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StatusDot, StatusTone } from '@shared/badge/status-dot/status-dot';
import { UsageOutcomeDto } from '@domain/api/usage-report';
import { formatCount, formatPercent, formatTokens } from './usage-format';

/** The board's geometry: a 150px box, a radius-56 ring at stroke width 17. */
const RADIUS = 56;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

/** The outcomes in the order the legend lists them, with the tone each arc takes. */
const OUTCOME_TONES: ReadonlyArray<{ status: string; label: string; tone: StatusTone }> = [
  { status: 'Completed', label: 'Completed', tone: 'ok' },
  { status: 'Cancelled', label: 'Cancelled', tone: 'muted' },
  { status: 'Failed', label: 'Failed', tone: 'warn' },
];

/**
 * Frame `5j`'s turn-outcome donut, with the combined cancelled-and-failed share at its centre.
 *
 * Four concentric `<circle>` elements with `stroke-dasharray`, exactly as the board draws them —
 * one track and one arc per outcome, each offset by the arcs before it.
 *
 * **The arcs are decorative and the legend is the data.** Three colours on a ring cannot be read
 * by anyone who does not see them as three colours (SC 1.4.1), and a donut has no accessible
 * geometry to expose, so every arc is also a legend row carrying its name, its count and its
 * share. The ring is `aria-hidden` and the panel is described by the figures beside it.
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

    // Every outcome gets a legend row whether or not it occurred, because a missing row reads as a
    // category this deployment does not have rather than one that stayed at zero this period. The
    // *arcs* still skip a zero, since a zero-length arc is nothing to draw.
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
        // Filtered on the datum, before the geometry: a zero-length arc is nothing to draw, and
        // deciding that from the formatted dash-array would couple it to how `round` happens to
        // print. `consumed` is unaffected, because a zero-turn outcome advances it by zero.
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

  /**
   * The centre figure: the share of turns that produced nothing anyone read.
   *
   * Cancelled and failed together rather than separately, because they are the same fact from the
   * reader's side — the answer never arrived — and the split is one glance away in the legend.
   */
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
