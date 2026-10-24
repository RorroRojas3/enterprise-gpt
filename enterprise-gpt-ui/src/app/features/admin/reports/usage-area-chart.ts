import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { UsageSeriesPointDto } from '@core/api/usage-report.model';
import { formatDay, formatTokens } from './usage-format';

/** A fixed viewBox: the chart scales to its column, the coordinates never change. */
const VIEW_WIDTH = 640;
const VIEW_HEIGHT = 170;
const PLOT_TOP = 10;
const PLOT_BOTTOM = 152;

const AXIS_LABELS = 5;

/** Per-instance gradient ids: `<defs>` ids are document-global, and a duplicate fails axe. */
let gradientSequence = 0;

/**
 * Hand-authored SVG: a charting library would add its bundle to the shared `admin-routes` chunk
 * for eleven line segments. No smoothing, as a spline would invent values between measured days.
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

    // A floor of one, so a range with no traffic draws a flat baseline instead of dividing by zero.
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

    // A lone moveto is not stroked (SVG 2 §13), so a one-day window gets a zero-length segment
    // that a round cap paints.
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

  /** Evenly spaced dates, not one per point. */
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

  /** Read instead of the `aria-hidden` line: its span, total and peak are what is describable. */
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
