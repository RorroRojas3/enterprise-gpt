import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { UsageOutcomeDto } from '@domain/api/usage-report';
import { OutcomeDonut } from './outcome-donut';

const BOARD_OUTCOMES: UsageOutcomeDto[] = [
  { status: 'Completed', turns: 1710, totalTokens: 37_600_000 },
  { status: 'Cancelled', turns: 285, totalTokens: 6_300_000 },
  { status: 'Failed', turns: 198, totalTokens: 4_300_000 },
];

describe('OutcomeDonut (US-1302)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function render(outcomes: UsageOutcomeDto[]): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(OutcomeDonut);
    fixture.componentRef.setInput('outcomes', outcomes);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('labels the centre with the combined cancelled-and-failed share', async () => {
    const host = await render(BOARD_OUTCOMES);

    // (285 + 198) / 2193 = 22%, which is the figure frame 5j draws.
    expect(host.querySelector('.donut__centre')?.textContent?.trim()).toBe('22%');
    expect(host.querySelector('.donut__caption')?.textContent?.trim()).toBe('cancelled + failed');
  });

  it('states what those turns cost, which is the number the panel exists for', async () => {
    const host = await render(BOARD_OUTCOMES);

    expect(host.querySelector('.donut__wasted')?.textContent).toContain('10.6M tokens');
    // Also the share, because the ring that draws it is hidden from assistive technology.
    expect(host.querySelector('.donut__wasted')?.textContent).toContain('22%');
    expect(host.textContent).toContain('cancelled or failed');
  });

  it('backs every arc with a legend row carrying its count and share', async () => {
    const host = await render(BOARD_OUTCOMES);
    const rows = [...host.querySelectorAll('.donut__row')];

    // Colour is never the only channel (SC 1.4.1): three tones on a ring say nothing on their own.
    expect(rows).toHaveLength(3);
    expect(rows[0]!.textContent).toContain('Completed');
    expect(rows[0]!.textContent).toContain('1,710 · 78%');
    expect(rows[1]!.textContent).toContain('285 · 13%');
    expect(rows[2]!.textContent).toContain('198 · 9%');
  });

  it('lays the arcs end to end around the ring', async () => {
    const host = await render(BOARD_OUTCOMES);
    const arcs = [...host.querySelectorAll('.donut__arc')];

    expect(arcs).toHaveLength(3);
    // The first starts at the top; each subsequent one is offset by everything drawn before it.
    expect(arcs[0]!.getAttribute('stroke-dashoffset')).toBe('0');
    expect(Number(arcs[1]!.getAttribute('stroke-dashoffset'))).toBeLessThan(0);
    expect(Number(arcs[2]!.getAttribute('stroke-dashoffset'))).toBeLessThan(
      Number(arcs[1]!.getAttribute('stroke-dashoffset')),
    );
  });

  it('keeps a legend row for an outcome that did not occur, but draws no arc for it', async () => {
    const host = await render([{ status: 'Completed', turns: 10, totalTokens: 500 }]);

    // A missing row reads as a category this deployment does not have, rather than one that
    // stayed at zero — while a zero-length arc is simply nothing to draw.
    expect(host.querySelectorAll('.donut__row')).toHaveLength(3);
    expect(host.querySelectorAll('.donut__arc')).toHaveLength(1);
    expect(host.querySelector('.donut__centre')?.textContent?.trim()).toBe('0%');
  });

  it('draws no centre figure at all when nothing ran', async () => {
    const host = await render([]);

    expect(host.querySelector('.donut__centre')).toBeNull();
    expect(host.querySelectorAll('.donut__arc')).toHaveLength(0);
  });

  it('hides the ring from assistive technology and names the panel from the figures', async () => {
    const host = await render(BOARD_OUTCOMES);

    expect(host.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
    // The list is named, not narrated: its own rows already carry the figures, and repeating them
    // in the label would have a screen reader read the whole breakdown twice.
    expect(host.querySelector('.donut__legend')?.getAttribute('aria-label')).toBe('Turn outcomes');
  });
});
