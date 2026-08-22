import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { Component, signal } from '@angular/core';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from '@core/session/session-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpServerFixture, modelFixture } from '@testing/catalog';
import { NARROW_VIEWPORT, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { usageReportFixture } from '@testing/reports';
import { directoryUserFixture, flushPermissions, userPage } from '@testing/users';
import { AdminLayout } from './admin-layout';
import { adminRoutes } from './admin.routes';

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;
const MODELS_URL = `${TEST_API_BASE_URL}/api/models/all`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps/all`;
const REPORTS_URL = `${TEST_API_BASE_URL}/api/reports/usage`;

/** Stands in for the chat route an unmatched URL used to be ejected to. */
@Component({ template: 'chat' })
class ChatStub {}

describe('AdminLayout (US-1201, completed by US-1209)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    resetMediaQueries();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SessionStore, useValue: { user: signal(null) } },
        // The real child routes, so the redirect and the rail are exercised as shipped.
        // The chat route and the trailing `**` mirror `app.routes.ts`, which is what an
        // unmatched admin URL falls to when the area does not absorb it itself — without
        // them that navigation would merely reject, and the test below could not tell
        // "stayed in the admin area" apart from "went nowhere".
        provideRouter(
          [
            { path: 'admin', children: adminRoutes },
            { path: 'chat', component: ChatStub },
            { path: '**', redirectTo: 'chat' },
          ],
          withComponentInputBinding(),
        ),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    TestBed.inject(Router).setUpLocationChangeListener();
  });

  afterEach(() => {
    resetMediaQueries();
    backend.verify();
  });

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  /** Opens a URL under /admin and answers whichever tab's first query it issues. */
  async function open(url: string): Promise<void> {
    await harness.navigateByUrl(url, AdminLayout);

    if (url.includes('/models')) {
      backend.expectOne(MODELS_URL).flush([modelFixture()]);
    } else if (url.includes('/mcps')) {
      backend.expectOne(MCPS_URL).flush([mcpServerFixture()]);
    } else if (url.includes('/reports')) {
      backend.expectOne((request) => request.url === REPORTS_URL).flush(usageReportFixture());
    } else {
      // The users tab also asks for the permission catalog, for its filter (US-1206).
      flushPermissions(backend, undefined, { optional: true });
      backend
        .expectOne((request) => request.url === USERS_URL)
        .flush(userPage([directoryUserFixture()]));
    }

    await harness.fixture.whenStable();
  }

  it('redirects /admin to the users tab', async () => {
    await open('/admin');

    expect(TestBed.inject(Router).url).toBe('/admin/users');
  });

  it('marks the open tab from the router rather than from local state', async () => {
    await open('/admin/users');

    const active = element().querySelector('.admin-nav__link--active');
    expect(active?.textContent).toContain('Users');
    // A styled link is not a navigation state: the rail has to say which one is current.
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('lists only the tabs that exist', async () => {
    await open('/admin/users');

    const labels = [...element().querySelectorAll('.admin-nav__link')].map((link) =>
      link.textContent?.trim(),
    );
    // A rail entry leading nowhere is the shown-and-disabled affordance US-203 rejected,
    // so each of these arrived in the same commit as its route — US-1208 the third,
    // US-1209 the fourth. The board draws exactly these four, in this order.
    expect(labels).toEqual(['Users', 'Models', 'MCP servers', 'Reports']);
  });

  it('becomes the pill strip below 768px', async () => {
    setMediaQuery(NARROW_VIEWPORT, true);
    await open('/admin/users');

    expect(element().querySelector('app-pill-subnav')).not.toBeNull();
    expect(element().querySelector('.admin-nav')).toBeNull();
  });

  it('reaches every tab by URL below 768px too, where the rail is a pill strip', async () => {
    setMediaQuery(NARROW_VIEWPORT, true);
    await open('/admin/reports');

    // `ADMIN_PILLS` is derived from `ADMIN_TABS`, so the fourth entry arrives here for
    // free — but criterion 1 is about the rail marking the open tab, and below the
    // breakpoint the rail *is* this strip.
    const active = element().querySelector('.pill-subnav__pill--active');
    expect(active?.textContent).toContain('Reports');
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('renders the users tab inside the layout', async () => {
    await open('/admin/users');

    // The store is provided on the child route, not on the layout: US-1209 requires that
    // opening one tab instantiates that tab's store and no other.
    expect(element().querySelector('app-admin-users')).not.toBeNull();
  });

  it('opens the models tab by URL, instantiating only its own store (US-1207)', async () => {
    await open('/admin/models');

    expect(element().querySelector('app-admin-models')).not.toBeNull();
    // The users tab's store is provided on *its* route, so nothing here should have
    // asked the directory for anything. `backend.verify()` in afterEach is the other
    // half of this assertion.
    expect(element().querySelector('app-admin-users')).toBeNull();

    const active = element().querySelector('.admin-nav__link--active');
    expect(active?.textContent).toContain('Models');
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('opens the MCP servers tab by URL, instantiating only its own store (US-1208)', async () => {
    await open('/admin/mcps');

    expect(element().querySelector('app-admin-mcps')).not.toBeNull();
    // Neither sibling tab's store is provided on the layout, so nothing here should have
    // asked for users or models. `backend.verify()` in afterEach is the other half.
    expect(element().querySelector('app-admin-users')).toBeNull();
    expect(element().querySelector('app-admin-models')).toBeNull();

    const active = element().querySelector('.admin-nav__link--active');
    expect(active?.textContent).toContain('MCP servers');
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('opens the reports tab by URL, instantiating only its own store (US-1302)', async () => {
    await open('/admin/reports');

    // Its own request is answered in `open`; that no sibling asked for anything is what
    // `backend.verify()` in afterEach asserts. Until US-1302 this tab had no store at all,
    // because it had no endpoint to call.
    expect(element().querySelector('app-admin-reports')).not.toBeNull();
    expect(element().querySelector('app-admin-users')).toBeNull();
    expect(element().querySelector('app-admin-models')).toBeNull();
    expect(element().querySelector('app-admin-mcps')).toBeNull();

    const active = element().querySelector('.admin-nav__link--active');
    expect(active?.textContent).toContain('Reports');
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('keeps an unknown admin URL inside the admin area rather than ejecting it', async () => {
    // Without the layout's own `**` child the router backtracks out of /admin and falls
    // to the table's, which lands an administrator on /chat — the outcome the ChatStub
    // route above exists to make visible rather than merely absent.
    await open('/admin/mistyped');

    expect(TestBed.inject(Router).url).toBe('/admin/users');
  });

  it('restores the previous tab on back (US-1209)', async () => {
    await open('/admin/users');
    await open('/admin/models');

    TestBed.inject(Location).back();
    // A macrotask: the location mock notifies asynchronously, so `whenStable` alone runs
    // before the router has heard about it.
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
    flushPermissions(backend, undefined, { optional: true });
    backend
      .expectOne((request) => request.url === USERS_URL)
      .flush(userPage([directoryUserFixture()]));
    await harness.fixture.whenStable();

    // Tabs are routes, not local state, so this is the router's behaviour rather than
    // anything the layout implements.
    expect(TestBed.inject(Router).url).toBe('/admin/users');
  });
});
