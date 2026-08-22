import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { KpiTile } from './kpi-tile';

describe('KpiTile (US-1302)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function render(delta: number | null): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(KpiTile);
    fixture.componentRef.setInput('label', 'Total tokens');
    fixture.componentRef.setInput('value', '48.2M');
    fixture.componentRef.setInput('delta', delta);
    fixture.componentRef.setInput('comparison', 'prior 30 days');
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('states the figure, the change and what it is measured against', async () => {
    const host = await render(12);

    expect(host.querySelector('.kpi__label')?.textContent?.trim()).toBe('Total tokens');
    expect(host.querySelector('.kpi__value')?.textContent?.trim()).toBe('48.2M');
    expect(host.querySelector('.kpi__change')?.textContent?.trim()).toBe('+12%');
    expect(host.textContent).toContain('vs. prior 30 days');
  });

  it('signs the direction in text as well as in the arrow and the tone', async () => {
    // Colour is never the only channel (SC 1.4.1), and neither is a glyph.
    const up = await render(12);
    expect(up.querySelector('.kpi__delta--up')).not.toBeNull();
    expect(up.querySelector('use')?.getAttribute('href')).toBe('#bi-arrow-up-right');

    const down = await render(-3);
    expect(down.querySelector('.kpi__change')?.textContent?.trim()).toBe('-3%');
    expect(down.querySelector('.kpi__delta--up')).toBeNull();
    expect(down.querySelector('use')?.getAttribute('href')).toBe('#bi-arrow-down-right');
  });

  it('draws no arrow at all for an unchanged figure', async () => {
    const host = await render(0);

    // `bi-arrow-down-right` beside the text "0%" would announce a decrease that did not happen,
    // and the arrow is the non-colour channel the tile leans on.
    expect(host.querySelector('use')).toBeNull();
    expect(host.querySelector('.kpi__change')?.textContent?.trim()).toBe('0%');
  });

  it('says there was nothing to compare against rather than showing a change of zero', async () => {
    const host = await render(null);

    expect(host.textContent).toContain('No prior 30 days to compare with');
    expect(host.querySelector('.kpi__change')).toBeNull();
  });
});
