import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { UsageSeriesPointDto } from '@domain/api/usage-report';
import { usageSeriesFixture } from '@testing/reports';
import { UsageAreaChart } from './usage-area-chart';

describe('UsageAreaChart (US-1302)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function render(points: readonly UsageSeriesPointDto[]): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(UsageAreaChart);
    fixture.componentRef.setInput('points', points);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('draws one straight segment per day, with no smoothing', async () => {
    const host = await render(usageSeriesFixture(11));
    const line = host.querySelector('.area__line')?.getAttribute('d') ?? '';

    // A spline through daily totals would invent values between the days that were measured,
    // which on a spend report is a number somebody could act on.
    expect(line).not.toContain('C');
    expect(line).not.toContain('Q');
    expect(line.match(/L/g)).toHaveLength(10);
    expect(line.startsWith('M0,')).toBe(true);
  });

  it('closes the fill down to the baseline and back', async () => {
    const host = await render(usageSeriesFixture(5));
    const area = host.querySelector('.area__fill')?.getAttribute('d') ?? '';

    expect(area.endsWith('Z')).toBe(true);
    expect(area).toContain(',152');
  });

  it('scales against the busiest day, so the peak reaches the top of the plot', async () => {
    const host = await render([
      { date: '2026-08-01', totalTokens: 0, estimatedCost: null },
      { date: '2026-08-02', totalTokens: 100, estimatedCost: null },
    ]);
    const line = host.querySelector('.area__line')?.getAttribute('d') ?? '';

    expect(line).toContain('M0,152');
    expect(line).toContain('L640,10');
  });

  it('draws a flat baseline for a range with no traffic rather than dividing by zero', async () => {
    const host = await render([
      { date: '2026-08-01', totalTokens: 0, estimatedCost: null },
      { date: '2026-08-02', totalTokens: 0, estimatedCost: null },
    ]);

    expect(host.querySelector('.area__line')?.getAttribute('d')).toBe('M0,152 L640,152');
  });

  it('closes a single-day window into a segment a round cap can paint', async () => {
    const host = await render([{ date: '2026-08-01', totalTokens: 42, estimatedCost: null }]);
    const line = host.querySelector('.area__line');

    // SVG 2 §13: a subpath with no line-drawing command is not stroked, so a lone `M320,10` would
    // paint nothing however it is capped. The zero-length segment is what makes the cap apply.
    expect(line?.getAttribute('d')).toBe('M320,10 L320,10');
    expect(line?.getAttribute('stroke-linecap')).toBe('round');
  });

  it('keeps the stroke an even width, though the viewBox is stretched to the column', async () => {
    const host = await render(usageSeriesFixture(5));

    // `preserveAspectRatio="none"` scales x and y differently, so without this the horizontal runs
    // render thinner than the vertical ones — thin enough on a narrow card to undercut the 3:1 the
    // stroke's colour was chosen for.
    expect(host.querySelector('.area__line')?.getAttribute('vector-effect')).toBe(
      'non-scaling-stroke',
    );
  });

  it('carries five evenly spaced axis labels rather than one per day', async () => {
    const host = await render(usageSeriesFixture(31, '2026-07-09'));
    const labels = [...host.querySelectorAll('.area__axis span')].map((span) => span.textContent);

    expect(labels).toHaveLength(5);
    expect(labels[0]).toBe('Jul 9');
    expect(labels[4]).toBe('Aug 8');
  });

  it('shows every label when there are fewer days than the strip holds', async () => {
    const host = await render(usageSeriesFixture(3, '2026-08-01'));

    expect(host.querySelectorAll('.area__axis span')).toHaveLength(3);
  });

  it('gives the plot an accessible summary instead of a shape nobody can hear', async () => {
    const host = await render([
      { date: '2026-08-01', totalTokens: 10, estimatedCost: null },
      { date: '2026-08-02', totalTokens: 90, estimatedCost: null },
    ]);
    const label = host.querySelector('figure')?.getAttribute('aria-label') ?? '';

    expect(host.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
    expect(label).toContain('2 days from Aug 1 to Aug 2');
    expect(label).toContain('100 tokens in total');
    expect(label).toContain('peaking at 90 on Aug 2');
  });

  it('gives each instance its own gradient id, because the id is document-global', async () => {
    const first = await render(usageSeriesFixture(3));
    const second = await render(usageSeriesFixture(3));

    const firstId = first.querySelector('linearGradient')?.getAttribute('id');
    const secondId = second.querySelector('linearGradient')?.getAttribute('id');

    // A fixed id would be a duplicate-id violation the moment two charts render, and would
    // silently re-point the first chart's fill at the second chart's gradient.
    expect(firstId).toBeTruthy();
    expect(firstId).not.toBe(secondId);
    expect(first.querySelector('.area__fill')?.getAttribute('fill')).toBe(`url(#${firstId})`);
  });
});
