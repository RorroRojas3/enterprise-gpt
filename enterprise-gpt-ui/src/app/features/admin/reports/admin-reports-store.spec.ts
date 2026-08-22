import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { Dispatcher } from '@ngrx/signals/events';
import { sessionEvents } from '@core/events/session-events';
import { provideTestAppConfig, TEST_API_BASE_URL } from '@testing/app-config';
import { emptyUsageReportFixture, usageGroupFixture, usageReportFixture } from '@testing/reports';
import { UsageGrouping, UsageRange } from './admin-reports-route';
import { ReportsStore } from './admin-reports-store';

const REPORTS_URL = `${TEST_API_BASE_URL}/api/reports/usage`;

describe('ReportsStore (US-1302)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ReportsStore>;
  let range: ReturnType<typeof signal<UsageRange>>;
  let grouping: ReturnType<typeof signal<UsageGrouping>>;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        ReportsStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ReportsStore);
    range = signal<UsageRange>(30);
    grouping = signal<UsageGrouping>('model');
  });

  afterEach(() => {
    backend.verify();
  });

  /** Wires the query the way the screen's constructor does — one computation, both parameters. */
  function bind(): void {
    TestBed.runInInjectionContext(() =>
      store.bindQuery(() => ({ range: range(), grouping: grouping() })),
    );
    TestBed.tick();
  }

  function expectReport(): TestRequest {
    return backend.expectOne((request) => request.url === REPORTS_URL);
  }

  async function settle(): Promise<void> {
    TestBed.tick();
    await Promise.resolve();
  }

  it('asks for the bound range and grouping in one request', async () => {
    grouping.set('user');
    bind();

    const request = expectReport();
    expect(request.request.params.get('groupBy')).toBe('user');
    expect(request.request.params.get('from')).toBeTruthy();
    // Never a `to`: the end of the window is the server's own clock, which is the clock the
    // rows were written by.
    expect(request.request.params.has('to')).toBe(false);

    request.flush(usageReportFixture({ groupBy: 'user' }));
    await settle();

    expect(store.isFulfilled()).toBe(true);
    expect(store.report()?.groupBy).toBe('user');
  });

  it('sends a window the requested number of days wide', async () => {
    range.set(7);
    bind();

    const request = expectReport();
    const from = new Date(request.request.params.get('from')!).getTime();
    const spanDays = (Date.now() - from) / 86_400_000;

    expect(spanDays).toBeGreaterThan(6.9);
    expect(spanDays).toBeLessThan(7.1);

    request.flush(usageReportFixture());
    await settle();
  });

  it('issues exactly one request when a control changes', async () => {
    bind();
    expectReport().flush(usageReportFixture());
    await settle();

    range.set(90);
    await settle();

    const second = expectReport();
    expect(second.request.params.get('groupBy')).toBe('model');
    second.flush(usageReportFixture());
    await settle();
  });

  it('returns to the first page when a new report lands', async () => {
    const groups = Array.from({ length: 12 }, (_, index) =>
      usageGroupFixture({ key: `g${index}`, label: `Model ${index}` }),
    );
    bind();
    expectReport().flush(usageReportFixture({ groups, topModels: groups.slice(0, 10) }));
    await settle();

    store.setPage(3);
    expect(store.page()).toBe(3);

    grouping.set('provider');
    await settle();
    expectReport().flush(usageReportFixture({ groupBy: 'provider', groups, topModels: groups }));
    await settle();

    // Page 3 of the old breakdown names rows the new one does not have.
    expect(store.page()).toBe(1);
  });

  it('pages the breakdown in memory and clamps at both ends', async () => {
    const groups = Array.from({ length: 12 }, (_, index) =>
      usageGroupFixture({ key: `g${index}`, label: `Model ${index}` }),
    );
    bind();
    expectReport().flush(usageReportFixture({ groups, topModels: groups.slice(0, 10) }));
    await settle();

    expect(store.totalPages()).toBe(3);
    expect(store.totalCount()).toBe(12);
    expect(store.pageSize()).toBe(5);
    expect(store.pagedGroups()).toHaveLength(5);

    store.setPage(3);
    expect(store.pagedGroups()).toHaveLength(2);

    store.setPage(99);
    expect(store.page()).toBe(3);

    store.setPage(0);
    expect(store.page()).toBe(1);
  });

  it('states the truncation in the count, so the table cannot silently disagree with the totals', async () => {
    const groups = Array.from({ length: 6 }, (_, index) => usageGroupFixture({ key: `g${index}` }));
    bind();
    expectReport().flush(
      usageReportFixture({ groups, topModels: groups, groupsTruncated: true, groupLimit: 6 }),
    );
    await settle();

    // The same sentence `Paginator` renders, so the live region and the visible footer cannot
    // disagree; why the breakdown is short is a separate notice.
    expect(store.countSummary()).toBe('Showing 1–5 of 6 models');
    expect(store.truncationNotice()).toContain('Only the 6 heaviest models are listed');
  });

  it('says the same sentence the paginator does, including for a single row', async () => {
    bind();
    const one = [usageGroupFixture({ key: 'only' })];
    expectReport().flush(usageReportFixture({ groups: one, topModels: one }));
    await settle();

    // `Paginator.itemLabel` has no count to agree with, so both sides stay plural — otherwise the
    // visible footer and the announced summary would differ on a one-row breakdown.
    expect(store.countSummary()).toBe('Showing 1–1 of 1 models');
  });

  it('reads an empty range as empty rather than as unloaded', async () => {
    bind();
    expectReport().flush(emptyUsageReportFixture());
    await settle();

    expect(store.isEmpty()).toBe(true);
    expect(store.isFirstLoad()).toBe(false);
    expect(store.groups()).toHaveLength(0);
    expect(store.countSummary()).toBe('No models');
    expect(store.truncationNotice()).toBeNull();
  });

  it('stays empty while the next range is on the wire, rather than flashing a dashboard of zeroes', async () => {
    bind();
    expectReport().flush(emptyUsageReportFixture());
    await settle();

    range.set(90);
    await settle();
    const pending = expectReport();

    // Gated on `isFulfilled` this went false the moment the request started, and the screen fell
    // through to the dashboard branch and painted four zero tiles for the length of it.
    expect(store.isPending()).toBe(true);
    expect(store.isEmpty()).toBe(true);

    pending.flush(usageReportFixture());
    await settle();

    expect(store.isEmpty()).toBe(false);
  });

  it('keeps the rendered grouping while the next report is on the wire', async () => {
    bind();
    expectReport().flush(usageReportFixture({ groupBy: 'model' }));
    await settle();

    grouping.set('user');
    await settle();
    const pending = expectReport();

    // The rows on screen still belong to the previous report, so the table's own header has to
    // keep naming what they are until the next one replaces them.
    expect(store.renderedGrouping()).toBe('model');

    pending.flush(usageReportFixture({ groupBy: 'user' }));
    await settle();

    expect(store.renderedGrouping()).toBe('user');
  });

  it('cancels the flight and clears the report on sign-out', async () => {
    bind();
    const pending = expectReport();

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await settle();

    // Two separate guarantees, and the store composes both: `withResetOnSignOut` clears the state,
    // and `takeUntil` cancels the request — without which the previous user's report would land on
    // top of the cleared state a moment later. Clearing state is not cancelling work.
    expect(store.report()).toBeNull();
    expect(store.isIdle()).toBe(true);
    // The request itself is gone, not merely ignored — the previous user's report can never land
    // on top of the cleared state, because there is nothing left to deliver it.
    expect(pending.cancelled).toBe(true);
  });

  it('keeps the failure and offers a retry that re-asks the same question', async () => {
    grouping.set('day');
    bind();
    expectReport().flush({ title: 'Server error' }, { status: 500, statusText: 'Server Error' });
    await settle();

    expect(store.error()).not.toBeNull();
    expect(store.isFirstLoad()).toBe(false);

    store.retry();
    await settle();

    const retried = expectReport();
    expect(retried.request.params.get('groupBy')).toBe('day');
    retried.flush(usageReportFixture({ groupBy: 'day' }));
    await settle();

    expect(store.error()).toBeNull();
  });
});
