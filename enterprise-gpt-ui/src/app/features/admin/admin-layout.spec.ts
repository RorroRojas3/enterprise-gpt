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

/** Where an unmatched admin URL would land if the area did not absorb it. */
@Component({ template: 'chat' })
class ChatStub {}

describe('AdminLayout', () => {
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
        // The real child routes. The chat route and `**` mirror `app.routes.ts`, so a test can
        // tell "stayed in the admin area" from "went nowhere".
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
      // The users tab also asks for the permission catalog, for its filter.
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
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('lists only the tabs that exist', async () => {
    await open('/admin/users');

    const labels = [...element().querySelectorAll('.admin-nav__link')].map((link) =>
      link.textContent?.trim(),
    );
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

    const active = element().querySelector('.pill-subnav__pill--active');
    expect(active?.textContent).toContain('Reports');
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('renders the users tab inside the layout', async () => {
    await open('/admin/users');

    expect(element().querySelector('app-admin-users')).not.toBeNull();
  });

  it('opens the models tab by URL, instantiating only its own store', async () => {
    await open('/admin/models');

    expect(element().querySelector('app-admin-models')).not.toBeNull();
    // `backend.verify()` in afterEach asserts that no sibling tab's store issued a request.
    expect(element().querySelector('app-admin-users')).toBeNull();

    const active = element().querySelector('.admin-nav__link--active');
    expect(active?.textContent).toContain('Models');
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('opens the MCP servers tab by URL, instantiating only its own store', async () => {
    await open('/admin/mcps');

    expect(element().querySelector('app-admin-mcps')).not.toBeNull();
    // `backend.verify()` in afterEach asserts that no sibling tab's store issued a request.
    expect(element().querySelector('app-admin-users')).toBeNull();
    expect(element().querySelector('app-admin-models')).toBeNull();

    const active = element().querySelector('.admin-nav__link--active');
    expect(active?.textContent).toContain('MCP servers');
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('opens the reports tab by URL, instantiating only its own store', async () => {
    await open('/admin/reports');

    // `open` answers its request; `backend.verify()` in afterEach asserts no sibling made one.
    expect(element().querySelector('app-admin-reports')).not.toBeNull();
    expect(element().querySelector('app-admin-users')).toBeNull();
    expect(element().querySelector('app-admin-models')).toBeNull();
    expect(element().querySelector('app-admin-mcps')).toBeNull();

    const active = element().querySelector('.admin-nav__link--active');
    expect(active?.textContent).toContain('Reports');
    expect(active?.getAttribute('aria-current')).toBe('page');
  });

  it('keeps an unknown admin URL inside the admin area rather than ejecting it', async () => {
    await open('/admin/mistyped');

    expect(TestBed.inject(Router).url).toBe('/admin/users');
  });

  it('restores the previous tab on back', async () => {
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

    expect(TestBed.inject(Router).url).toBe('/admin/users');
  });
});
