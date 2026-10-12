import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { BulkActionBar } from './bulk-action-bar';

describe('BulkActionBar', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(count: number, note = '') {
    const fixture = TestBed.createComponent(BulkActionBar);
    fixture.componentRef.setInput('count', count);
    fixture.componentRef.setInput('note', note);
    await fixture.whenStable();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('stays out of the way until something is selected', async () => {
    const { host } = await render(0);

    expect(host.querySelector('.bulk-bar')).toBeNull();
  });

  it('announces the count politely rather than taking focus', async () => {
    const activeBefore = document.activeElement;
    const { host } = await render(24, 'Select-all covers the 24 loaded rows only');

    expect(host.querySelector('[aria-live="polite"]')?.textContent).toContain('24 selected');
    expect(host.textContent).toContain('loaded rows only');
    expect(document.activeElement).toBe(activeBefore);
  });

  it('names itself as a region so it can be reached deliberately', async () => {
    const { host } = await render(3);

    expect(host.querySelector('[role="region"]')?.getAttribute('aria-label')).toBe('Bulk actions');
  });
});
