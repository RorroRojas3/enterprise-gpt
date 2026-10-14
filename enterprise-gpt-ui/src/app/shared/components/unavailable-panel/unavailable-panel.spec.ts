import { TestBed } from '@angular/core/testing';
import { getByRole, queryAllByRole, queryByRole } from '@testing-library/dom';
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

    const region = getByRole(host, 'region', { name: "Your documents aren't available yet" });
    expect(region.textContent).toContain('not enabled for this deployment');
  });

  it('offers nothing to press — a missing API does not come back on retry', async () => {
    const host = await render();

    expect(queryByRole(host, 'button')).toBeNull();
    expect(queryByRole(host, 'link')).toBeNull();
  });

  it('is not a live region: it is the steady state of the screen, not news', async () => {
    const host = await render();

    expect(queryByRole(host, 'alert')).toBeNull();
    expect(queryByRole(host, 'status')).toBeNull();
    expect(host.querySelector('[aria-live]')).toBeNull();
  });

  it('renders no rows, records or placeholder values', async () => {
    const host = await render();

    for (const role of ['table', 'row', 'listitem']) {
      expect(queryAllByRole(host, role)).toHaveLength(0);
    }
  });
});
