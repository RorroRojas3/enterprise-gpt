import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { UsageSeriesPointDto } from '@domain/api/usage-report';
import { formatDay, formatTokens } from './usage-format';

/** Frame `5j`'s own viewBox. The chart scales to its column; the coordinates never change. */
const VIEW_WIDTH = 640;
const VIEW_HEIGHT = 170;
const PLOT_TOP = 10;
const PLOT_BOTTOM = 152;

/** How many x-axis labels the strip under the chart carries. The board draws five. */
const AXIS_LABELS = 5;

/**
 * Distinguishes one instance's gradient from another's.
 *
 * An SVG `<defs>` id is a document-global identifier, so a fixed one would be a `duplicate-id`
 * violation — serious impact, and the axe gate fails the build on it — the moment a second chart
 * renders. It would also silently re-point the first chart's fill at the second chart's gradient.
 */
let gradientSequence = 0;

/**
 * The usage-over-time area chart, frame `5j`.
 *
 * Hand-authored SVG rather than a charting library, which is what the design boards themselves
 * draw: two paths, no axes, no gridlines, no tooltips and no data markers. A library would add its
 * own bundle to the shared `admin-routes` chunk that `/admin/users` also downloads, to reproduce a
 * shape that is eleven line segments.
 *
 * The straight polyline is deliberate — no curve smoothing. A spline through daily totals invents
 * values between the days that were measured, which on a spend report is a number somebody could
 * act on.
 */
@Component({
  selector: 'app-usage-area-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-area-chart.html',
  styleUrl: './usage-area-chart.scss',
})
export class UsageAreaChart {
  readonly points = input.required<readonly UsageSeriesPointDto[]>();

  protected readonly viewBox = `0 0 ${VIEW_WIDTH} ${VIEW_HEIGHT}`;
  protected readonly gradientId = `usage-ridgefill-${++gradientSequence}`;

  private readonly _plotted = computed(() => {
    const series = this.points();
    if (series.length === 0) {
      return [];
    }

    // A floor of one, so a range with no traffic draws a flat baseline rather than dividing by
    // zero. The empty state replaces the whole panel in that case, but the guard has to hold for
    // the paint before it does.
    const peak = Math.max(1, ...series.map((point) => point.totalTokens));
    const span = series.length > 1 ? VIEW_WIDTH / (series.length - 1) : 0;

    return series.map((point, index) => ({
      x: series.length > 1 ? index * span : VIEW_WIDTH / 2,
      y: PLOT_BOTTOM - (point.totalTokens / peak) * (PLOT_BOTTOM - PLOT_TOP),
    }));
  });

  protected readonly linePath = computed(() => {
    const plotted = this._plotted();
    const drawn = plotted
      .map((point, index) => `${index === 0 ? 'M' : 'L'}${round(point.x)},${round(point.y)}`)
      .join(' ');

    // A lone moveto is not stroked — SVG 2 §13 is explicit that a subpath with no line-drawing
    // command paints nothing, cap or no cap. A one-day window would otherwise render an empty
    // panel under an `aria-label` promising a chart, so it gets a zero-length segment, which a
    // round cap does paint. Unreachable through the range control; this is a presentational
    // component with a required input.
    return plotted.length === 1
      ? `${drawn} L${round(plotted[0]!.x)},${round(plotted[0]!.y)}`
      : drawn;
  });

  /** The line, closed down to the baseline and back, which is what the gradient fills. */
  protected readonly areaPath = computed(() => {
    const plotted = this._plotted();
    if (plotted.length === 0) {
      return '';
    }

    const last = plotted[plotted.length - 1]!;
    const first = plotted[0]!;

    return `${this.linePath()} L${round(last.x)},${PLOT_BOTTOM} L${round(first.x)},${PLOT_BOTTOM} Z`;
  });

  /** Five evenly spaced dates, not one per point — the board's own axis. */
  protected readonly axisLabels = computed(() => {
    const series = this.points();
    if (series.length <= AXIS_LABELS) {
      return series.map((point) => formatDay(point.date));
    }

    return Array.from({ length: AXIS_LABELS }, (_, index) => {
      const at = Math.round((index / (AXIS_LABELS - 1)) * (series.length - 1));

      return formatDay(series[at]!.date);
    });
  });

  /**
   * What a screen reader is told instead of the shape.
   *
   * The line itself is `aria-hidden`: a path of eleven coordinates is not describable, and the
   * useful facts about it are the span, the total and the peak. The table further down the page
   * carries the per-group figures, so this is a summary rather than the only route to the data.
   */
  protected readonly summary = computed(() => {
    const series = this.points();
    if (series.length === 0) {
      return 'Usage over time: no data.';
    }

    const total = series.reduce((sum, point) => sum + point.totalTokens, 0);
    const peak = series.reduce((best, point) =>
      point.totalTokens > best.totalTokens ? point : best,
    );

    return (
      `Usage over time, ${series.length} days from ${formatDay(series[0]!.date)} ` +
      `to ${formatDay(series[series.length - 1]!.date)}. ${formatTokens(total)} tokens in total, ` +
      `peaking at ${formatTokens(peak.totalTokens)} on ${formatDay(peak.date)}.`
    );
  });
}

/** Two decimals is well under a device pixel at this scale, and keeps the `d` attribute short. */
function round(value: number): number {
  return Math.round(value * 100) / 100;
}
