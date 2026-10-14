import { TestBed } from '@angular/core/testing';
import { getByText, queryByText } from '@testing-library/dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { iconOf } from '@testing/overlays';
import { KpiTile } from './kpi-tile';

describe('KpiTile', () => {
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

    expect(getByText(host, 'Total tokens')).toBeTruthy();
    expect(getByText(host, '48.2M')).toBeTruthy();
    expect(getByText(host, '+12%')).toBeTruthy();
    expect(host.textContent).toContain('vs. prior 30 days');
  });

  it('signs the direction in text as well as in the arrow and the tone', async () => {
    // Colour is never the only channel (SC 1.4.1), and neither is a glyph.
    const up = await render(12);
    expect(iconOf(up)).toBe('bi-arrow-up-right');
    expect(up.querySelector('app-icon')?.classList).toContain('text-success-emphasis');

    const down = await render(-3);
    expect(getByText(down, '-3%')).toBeTruthy();
    expect(iconOf(down)).toBe('bi-arrow-down-right');
    expect(down.querySelector('app-icon')?.classList).not.toContain('text-success-emphasis');
  });

  it('draws no arrow at all for an unchanged figure', async () => {
    const host = await render(0);

    // A down arrow beside "0%" would announce a decrease that did not happen.
    expect(host.querySelector('use')).toBeNull();
    expect(getByText(host, '0%')).toBeTruthy();
  });

  it('says there was nothing to compare against rather than showing a change of zero', async () => {
    const host = await render(null);

    expect(host.textContent).toContain('No prior 30 days to compare with');
    expect(queryByText(host, /%$/)).toBeNull();
  });
});
