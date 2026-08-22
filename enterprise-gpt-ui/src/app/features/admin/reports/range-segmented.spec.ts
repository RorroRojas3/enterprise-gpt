import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { UsageRange } from './admin-reports-route';
import { RangeSegmented } from './range-segmented';

describe('RangeSegmented (US-1302)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function render(value: UsageRange = 30) {
    const fixture = TestBed.createComponent(RangeSegmented);
    fixture.componentRef.setInput('value', value);
    const selected = vi.fn();
    fixture.componentInstance.selected.subscribe(selected);
    await fixture.whenStable();

    return { host: fixture.nativeElement as HTMLElement, selected, fixture };
  }

  it('offers the three ranges the board draws, as real buttons', async () => {
    const { host } = await render();
    const buttons = [...host.querySelectorAll('button')];

    // The prototype draws spans; a control that changes what the page shows has to be
    // reachable, pressable and announced as the toggle it is.
    expect(buttons.map((button) => button.textContent?.trim())).toEqual([
      '7 days',
      '30 days',
      '90 days',
    ]);
    expect(host.querySelector('[role="group"]')?.getAttribute('aria-label')).toBe(
      'Reporting period',
    );
  });

  it('announces which range is in force', async () => {
    const { host } = await render(90);
    const pressed = [...host.querySelectorAll('button')].filter(
      (button) => button.getAttribute('aria-pressed') === 'true',
    );

    expect(pressed).toHaveLength(1);
    expect(pressed[0]!.textContent?.trim()).toBe('90 days');
    expect(pressed[0]!.classList.contains('segmented__option--active')).toBe(true);
  });

  it('emits the range a reader chose', async () => {
    const { host, selected } = await render(30);

    [...host.querySelectorAll('button')][2]!.click();

    expect(selected).toHaveBeenCalledWith(90);
  });

  it('says nothing when the range already in force is chosen again', async () => {
    const { host, selected } = await render(30);

    [...host.querySelectorAll('button')][1]!.click();

    // Otherwise every re-press would cost a history entry and a request for the same figures.
    expect(selected).not.toHaveBeenCalled();
  });

  it('stays operable while a report is in flight', async () => {
    const { host, selected } = await render(30);

    // A control that goes inert under the gesture that triggered it blurs itself, which drops a
    // keyboard reader to the top of the document. The store's `switchMap` handles the overlap.
    expect([...host.querySelectorAll('button')].some((button) => button.disabled)).toBe(false);

    [...host.querySelectorAll('button')][0]!.click();
    expect(selected).toHaveBeenCalledWith(7);
  });
});
