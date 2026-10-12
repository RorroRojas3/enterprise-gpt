import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { UnavailablePanel } from './unavailable-panel';

describe('UnavailablePanel', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render() {
    const fixture = TestBed.createComponent(UnavailablePanel);
    fixture.componentRef.setInput('heading', "Your documents aren't available yet");
    fixture.componentRef.setInput(
      'message',
      'The documents API is not enabled for this deployment.',
    );
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('names the missing capability', async () => {
    const host = await render();

    expect(host.textContent).toContain("Your documents aren't available yet");
    expect(host.textContent).toContain('not enabled for this deployment');
  });

  it('offers nothing to press — a missing API does not come back on retry', async () => {
    const host = await render();

    expect(host.querySelector('button')).toBeNull();
    expect(host.querySelector('a')).toBeNull();
  });

  it('is not a live region: it is the steady state of the screen, not news', async () => {
    const host = await render();

    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(host.querySelector('[aria-live]')).toBeNull();
  });

  it('renders no rows, records or placeholder values', async () => {
    const host = await render();

    expect(host.querySelectorAll('table, tr, li')).toHaveLength(0);
  });
});
