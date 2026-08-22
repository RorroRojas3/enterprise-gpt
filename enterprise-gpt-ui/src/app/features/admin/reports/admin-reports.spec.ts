import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { TABLET_VIEWPORT_QUERY, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { emptyUsageReportFixture, usageGroupFixture, usageReportFixture } from '@testing/reports';
import { AdminReports } from './admin-reports';
import {
  DEFAULT_USAGE_GROUPING,
  DEFAULT_USAGE_RANGE,
  toUsageGrouping,
  toUsageRange,
} from './admin-reports-route';

const REPORTS_URL = `${TEST_API_BASE_URL}/api/reports/usage`;

describe('AdminReports (US-1302)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(
          [
            { path: 'admin/reports', component: AdminReports },
            { path: 'admin/users', component: AdminReports },
          ],
          withComponentInputBinding(),
        ),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    // The harness navigates directly and never starts the router's popstate listener, so without
    // this a back press moves `Location` and leaves the router where it was.
    TestBed.inject(Router).setUpLocationChangeListener();
  });

  afterEach(() => {
    resetMediaQueries();
    backend.verify();
  });

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function expectReport(): TestRequest {
    return backend.expectOne((request) => request.url === REPORTS_URL);
  }

  /** The grid track set `DataTable` is rendering, which is what the column widths become. */
  function tracks(): string {
    return element()
      .querySelector<HTMLElement>('.data-table')!
      .style.getPropertyValue('--table-cols');
  }

  function url(): string {
    return TestBed.inject(Router).url;
  }

  async function open(target = '/admin/reports', report = usageReportFixture()): Promise<void> {
    await harness.navigateByUrl(target, AdminReports);
    expectReport().flush(report);
    await harness.fixture.whenStable();
  }

  /** Drives the group-by select the way a reader does, and answers the request it triggers. */
  async function chooseGrouping(grouping: string): Promise<void> {
    const select = element().querySelector<HTMLSelectElement>('#usage-group-by')!;
    select.value = grouping;
    select.dispatchEvent(new Event('change'));
    await harness.fixture.whenStable();
    expectReport().flush(usageReportFixture({ groupBy: grouping }));
    await harness.fixture.whenStable();
  }

  async function goBack(): Promise<void> {
    TestBed.inject(Location).back();
    await new Promise((resolve) => setTimeout(resolve));
    await harness.fixture.whenStable();
  }

  it('keeps the level-one heading it inherited from US-1209', async () => {
    await open();

    // Every sibling tab carries one, and `UnavailablePanel`'s heading was a `<p>`; replacing the
    // panel must not leave this the only routed screen with no level-one heading.
    expect(element().querySelector('h1')?.textContent?.trim()).toBe('Reports');
  });

  it('renders every panel frames 5j and 5k draw', async () => {
    await open();
    const host = element();

    expect(host.querySelectorAll('app-kpi-tile')).toHaveLength(4);
    expect(host.querySelector('app-range-segmented')).not.toBeNull();
    expect(host.querySelector('app-usage-area-chart')).not.toBeNull();
    expect(host.querySelector('app-usage-bar-list')).not.toBeNull();
    expect(host.querySelector('app-outcome-donut')).not.toBeNull();
    expect(host.querySelector('app-data-table')).not.toBeNull();
    expect(host.textContent).toContain('Usage over time');
    expect(host.textContent).toContain('Tokens by model');
    expect(host.textContent).toContain('Turn outcomes');
    expect(host.textContent).toContain('Tokens billed for answers nobody read');
  });

  it('offers no export control, as the board deliberately does not draw one', async () => {
    await open();

    expect(element().textContent).not.toContain('Export');
    expect(element().textContent).not.toContain('Download');
  });

  it('reads the date chip off the response rather than off the control', async () => {
    await open();

    // The window the figures were actually taken from, so the chip cannot claim a range the
    // report was not built from.
    expect(element().textContent).toContain('Jul 9 – Aug 8, 2026');
  });

  it('issues a request on every entry to the tab, including a back navigation (FR-41)', async () => {
    await open();

    await harness.navigateByUrl('/admin/users', AdminReports);
    expectReport().flush(usageReportFixture());
    await harness.fixture.whenStable();

    await goBack();

    // A route-scoped store is rebuilt on every entry, so nothing survives to be served stale.
    expectReport().flush(usageReportFixture());
    await harness.fixture.whenStable();
    expect(url()).toBe('/admin/reports');
  });

  it('reflects the range in the URL and asks again', async () => {
    await open();

    const buttons = [...element().querySelectorAll<HTMLButtonElement>('.segmented__option')];
    buttons.find((button) => button.textContent?.includes('90 days'))!.click();
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/reports?range=90');
    expectReport().flush(usageReportFixture());
    await harness.fixture.whenStable();
  });

  it('drops the range parameter again when the default is chosen, rather than spelling it out', async () => {
    await open('/admin/reports?range=7');

    const buttons = [...element().querySelectorAll<HTMLButtonElement>('.segmented__option')];
    buttons.find((button) => button.textContent?.includes('30 days'))!.click();
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/reports');
    expectReport().flush(usageReportFixture());
    await harness.fixture.whenStable();
  });

  it('reflects the grouping in the URL and asks again', async () => {
    await open();

    const select = element().querySelector<HTMLSelectElement>('#usage-group-by')!;
    select.value = 'user';
    select.dispatchEvent(new Event('change'));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/reports?group=user');
    const request = expectReport();
    expect(request.request.params.get('groupBy')).toBe('user');
    request.flush(usageReportFixture({ groupBy: 'user' }));
    await harness.fixture.whenStable();
  });

  it('restores the previous selection on back, without a line of code doing it', async () => {
    await open();

    const buttons = [...element().querySelectorAll<HTMLButtonElement>('.segmented__option')];
    buttons.find((button) => button.textContent?.includes('7 days'))!.click();
    await harness.fixture.whenStable();
    expectReport().flush(usageReportFixture());
    await harness.fixture.whenStable();

    await goBack();

    expect(url()).toBe('/admin/reports');
    expectReport().flush(usageReportFixture());
    await harness.fixture.whenStable();
  });

  it('reads a range it does not offer as the default, and asks for that', async () => {
    await harness.navigateByUrl('/admin/reports?range=5', AdminReports);

    const request = expectReport();
    const spanDays =
      (Date.now() - new Date(request.request.params.get('from')!).getTime()) / 86_400_000;
    expect(spanDays).toBeGreaterThan(29);

    request.flush(usageReportFixture());
    await harness.fixture.whenStable();

    // The control shows what is in force. A repaired parameter that left the URL saying `5` would
    // put the address bar and the segmented control in disagreement.
    const active = element().querySelector('.segmented__option--active');
    expect(active?.textContent?.trim()).toBe('30 days');
  });

  it('reads a grouping it does not offer as the default', async () => {
    await harness.navigateByUrl('/admin/reports?group=conversation', AdminReports);

    const request = expectReport();
    expect(request.request.params.get('groupBy')).toBe('model');

    request.flush(usageReportFixture());
    await harness.fixture.whenStable();

    const select = element().querySelector<HTMLSelectElement>('#usage-group-by')!;
    expect(select.value).toBe('model');
  });

  it('names the range in the empty state rather than drawing a chart of zeroes', async () => {
    await open('/admin/reports', emptyUsageReportFixture());
    const host = element();

    expect(host.querySelector('app-empty-state')).not.toBeNull();
    expect(host.textContent).toContain('No usage between Jul 9 and Aug 8, 2026');
    expect(host.querySelector('app-usage-area-chart')).toBeNull();
    expect(host.querySelector('app-kpi-tile')).toBeNull();
    expect(host.querySelector('app-outcome-donut')).toBeNull();
  });

  it('shows the traceId with a retry when the report fails', async () => {
    await harness.navigateByUrl('/admin/reports', AdminReports);
    expectReport().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Server Error',
    });
    await harness.fixture.whenStable();

    const panel = element().querySelector('app-error-panel');
    expect(panel).not.toBeNull();
    expect(panel?.textContent).toContain('Trace ID');
    // Instead of the dashboard, not beside it: a chart left on screen would be the last range the
    // reader looked at, labelled with this one.
    expect(element().querySelector('app-usage-area-chart')).toBeNull();

    element().querySelector<HTMLButtonElement>('app-error-panel button')!.click();
    await harness.fixture.whenStable();

    expectReport().flush(usageReportFixture());
    await harness.fixture.whenStable();
    expect(element().querySelector('app-error-panel')).toBeNull();
  });

  it('renames the table column after the grouping the rows belong to', async () => {
    await open('/admin/reports?group=user', usageReportFixture({ groupBy: 'user' }));

    expect(element().textContent).toContain('User');
  });

  it('gives the table proportional tracks below the desktop edge (US-1403)', async () => {
    await open();
    const desktop = tracks();

    setMediaQuery(TABLET_VIEWPORT_QUERY, true);
    await harness.fixture.whenStable();
    const tablet = tracks();

    // At desktop the six numeric columns are fixed, as frame 5j draws them. Below it they alone
    // are wider than the content pane, which would squeeze the name column to nothing and clip
    // every row — so they shrink together instead. Nothing is hidden: every column is a figure
    // the table exists to compare.
    expect(desktop).toContain('70px');
    expect(tablet).not.toContain('70px');
    expect(tablet).toContain('minmax(0, 1fr)');
  });

  it('pages the breakdown through the shared paginator, with its ends genuinely disabled', async () => {
    const groups = Array.from({ length: 7 }, (_, index) =>
      usageGroupFixture({ key: `g${index}`, label: `Model ${index}` }),
    );
    await open('/admin/reports', usageReportFixture({ groups, topModels: groups }));

    const steps = [...element().querySelectorAll<HTMLButtonElement>('.paginator__step')];
    expect(steps[0]!.disabled).toBe(true);
    expect(steps[steps.length - 1]!.disabled).toBe(false);
    expect(element().textContent).toContain('Showing 1–5 of 7 models');

    steps[steps.length - 1]!.click();
    await harness.fixture.whenStable();

    expect(element().textContent).toContain('Showing 6–7 of 7 models');
    // Paging is screen state: it must not put anything in the address bar, and must not refetch.
    expect(url()).toBe('/admin/reports');
  });

  it('states why a truncated breakdown does not add up to the totals above it', async () => {
    const groups = Array.from({ length: 3 }, (_, index) => usageGroupFixture({ key: `g${index}` }));
    await open(
      '/admin/reports',
      usageReportFixture({ groups, topModels: groups, groupsTruncated: true, groupLimit: 3 }),
    );

    expect(element().textContent).toContain('Only the 3 heaviest models are listed');
  });

  it('pushes the grouping on and off, and replaces a swap between two of them', async () => {
    await open();

    // On: a transition, so Back can undo it.
    await chooseGrouping('user');
    expect(url()).toBe('/admin/reports?group=user');

    // A swap: a refinement, so the walk to it costs no extra entry. A closed select commits on
    // every arrow key, and pushing each one would bury the reader under history.
    await chooseGrouping('provider');
    expect(url()).toBe('/admin/reports?group=provider');

    await goBack();

    // One Back, and it lands on the grouping before the walk rather than part-way through it.
    expect(url()).toBe('/admin/reports');
    expectReport().flush(usageReportFixture());
    await harness.fixture.whenStable();
  });
});

describe('admin-reports-route', () => {
  it('reads only the offered ranges, and anything else as the default', () => {
    expect(toUsageRange('7')).toBe(7);
    expect(toUsageRange('90')).toBe(90);
    expect(toUsageRange(undefined)).toBe(DEFAULT_USAGE_RANGE);
    expect(toUsageRange('5')).toBe(DEFAULT_USAGE_RANGE);
    expect(toUsageRange('')).toBe(DEFAULT_USAGE_RANGE);
    expect(toUsageRange('thirty')).toBe(DEFAULT_USAGE_RANGE);
  });

  it('survives a repeated key, which arrives as an array rather than a string', () => {
    // `withComponentInputBinding()` hands over the raw `Params` value, and a transform that threw
    // here would fail the navigation rather than the parameter.
    expect(toUsageRange(['7', '90'] as unknown as string)).toBe(DEFAULT_USAGE_RANGE);
    expect(toUsageGrouping(['user'] as unknown as string)).toBe(DEFAULT_USAGE_GROUPING);
  });

  it('reads only the offered groupings, case-insensitively', () => {
    expect(toUsageGrouping('user')).toBe('user');
    expect(toUsageGrouping('  DAY ')).toBe('day');
    expect(toUsageGrouping('conversation')).toBe(DEFAULT_USAGE_GROUPING);
    expect(toUsageGrouping(undefined)).toBe(DEFAULT_USAGE_GROUPING);
  });
});
