import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AdminReports } from './admin-reports';

describe('AdminReports (US-1209)', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The other half of "issues no request": nothing outstanding either.
    backend.verify();
  });

  async function render(): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(AdminReports);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('names the tab at level one, as its three siblings do', async () => {
    const host = await render();

    // The panel's own heading is a `<p>` bound through `aria-labelledby`, so without
    // this the screen would have no level-one heading at all.
    expect(host.querySelector('h1')?.textContent?.trim()).toBe('Reports');
  });

  it('names the missing capability, the reason, and what still happens without it', async () => {
    const host = await render();

    // Frame `5i`'s copy, and its last clause is load-bearing rather than consolation:
    // `ConversationUsage` rows are written for every turn, so nothing is being lost
    // while this tab waits for the route that reads them.
    expect(host.textContent).toContain('Usage reports aren’t available yet');
    expect(host.textContent).toContain('isn’t enabled for this deployment');
    expect(host.textContent).toContain('Token usage is still recorded');
  });

  it('offers nothing to press — a missing API does not come back on retry', async () => {
    const host = await render();

    expect(host.querySelector('button')).toBeNull();
    expect(host.querySelector('a')).toBeNull();
  });

  it('renders no rows, records or placeholder values (FR-49)', async () => {
    const host = await render();

    // The old client filled empty screens with sample data, which made "we have not
    // built this" indistinguishable from "your data is empty".
    expect(host.querySelectorAll('table, tr, td, li')).toHaveLength(0);
  });

  it('asks the network for nothing, because there is nothing to ask', async () => {
    await render();

    // US-1301 has not landed: `GET api/reports/usage` does not exist, and a request that
    // 404s would surface as an error panel rather than as an unavailable one.
    expect(backend.match(() => true)).toHaveLength(0);
  });
});
