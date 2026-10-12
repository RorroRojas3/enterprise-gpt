import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Paginator } from './paginator';

describe('Paginator', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(page: number, totalPages = 13, totalCount = 312, pageSize = 25) {
    const fixture = TestBed.createComponent(Paginator);
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('totalPages', totalPages);
    fixture.componentRef.setInput('totalCount', totalCount);
    fixture.componentRef.setInput('pageSize', pageSize);
    fixture.componentRef.setInput('itemLabel', 'users');
    await fixture.whenStable();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('disables the steps at the ends for real, not just visually', async () => {
    const first = await render(1);
    const steps = [...first.host.querySelectorAll<HTMLButtonElement>('.paginator__step')];
    expect(steps[0]?.disabled).toBe(true);
    expect(steps[1]?.disabled).toBe(false);

    const last = await render(13);
    const lastSteps = [...last.host.querySelectorAll<HTMLButtonElement>('.paginator__step')];
    expect(lastSteps[1]?.disabled).toBe(true);
  });

  it('marks the current page and makes every page a button', async () => {
    const { host } = await render(7);

    expect(host.querySelector('[aria-current="page"]')?.textContent?.trim()).toBe('7');
    expect(host.querySelectorAll('.paginator__page').length).toBeGreaterThan(0);
    expect(host.querySelectorAll('span.paginator__page')).toHaveLength(0);
  });

  it('hides the ellipsis from assistive technology', async () => {
    const { host } = await render(7);

    expect(host.querySelector('.paginator__gap')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('gets the range right on a short final page', async () => {
    const { host } = await render(13, 13, 312, 25);

    expect(host.querySelector('.paginator__summary')?.textContent).toContain(
      'Showing 301–312 of 312 users',
    );
  });

  it('says so plainly when there is nothing to page through', async () => {
    const { host } = await render(1, 0, 0, 25);

    expect(host.querySelector('.paginator__summary')?.textContent).toContain('No users');
  });

  it('emits the requested page and ignores a click on the current one', async () => {
    const { fixture, host } = await render(7);
    const changed = vi.fn();
    fixture.componentInstance.pageChanged.subscribe(changed);

    host.querySelector<HTMLButtonElement>('[aria-current="page"]')?.click();
    expect(changed).not.toHaveBeenCalled();

    host.querySelector<HTMLButtonElement>('[aria-label="Next page"]')?.click();
    expect(changed).toHaveBeenCalledWith(8);
  });

  it('offers no page-size select unless a caller asks for one', async () => {
    // Empty by default, rather than a set of sizes every caller has to opt out of.
    const { host } = await render(1);

    expect(host.querySelector('.paginator__size')).toBeNull();
  });

  it('shows the size in force, even before the reader touches the control', async () => {
    const { fixture, host } = await render(1, 4, 312, 100);
    fixture.componentRef.setInput('pageSizes', [25, 50, 100]);
    await fixture.whenStable();

    // A `[value]` on the <select> would run before `@for` inserted the options and leave the
    // first one showing, so a deep link would display a size it is not using.
    expect(host.querySelector<HTMLSelectElement>('.paginator__size select')?.value).toBe('100');
  });

  it('emits a chosen size and refuses one that is not on offer', async () => {
    const { fixture, host } = await render(1, 13, 312, 25);
    fixture.componentRef.setInput('pageSizes', [25, 50, 100]);
    await fixture.whenStable();

    const changed = vi.fn();
    fixture.componentInstance.pageSizeChanged.subscribe(changed);
    const select = host.querySelector<HTMLSelectElement>('.paginator__size select')!;

    select.value = '50';
    select.dispatchEvent(new Event('change'));
    expect(changed).toHaveBeenCalledWith(50);

    // The option list is the caller's; a size that is not on it must never reach a
    // request, where it would land as `take=7` or `take=NaN`.
    changed.mockClear();
    const rogue = document.createElement('option');
    rogue.value = '7';
    select.appendChild(rogue);
    select.value = '7';
    select.dispatchEvent(new Event('change'));

    expect(changed).not.toHaveBeenCalled();
  });

  it('lets a caller silence its summary when the table already announces it', async () => {
    const { fixture, host } = await render(1);
    expect(host.querySelector('.paginator__summary')?.getAttribute('aria-live')).toBe('polite');

    fixture.componentRef.setInput('liveSummary', false);
    await fixture.whenStable();

    // Two live regions carrying one sentence read the count twice on every page.
    expect(host.querySelector('.paginator__summary')?.hasAttribute('aria-live')).toBe(false);
  });
});
