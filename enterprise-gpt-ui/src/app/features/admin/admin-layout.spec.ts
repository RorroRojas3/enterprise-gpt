import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from '@core/session/session-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { NARROW_VIEWPORT, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { directoryUserFixture, userPage } from '@testing/users';
import { AdminLayout } from './admin-layout';
import { adminRoutes } from './admin.routes';

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;
const MODELS_URL = `${TEST_API_BASE_URL}/api/models/all`;

describe('AdminLayout (US-1201, ahead of US-1209)', () => {
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
        provideRouter([{ path: 'admin', children: adminRoutes }], withComponentInputBinding()),
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
    } else {
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
    // A rail entry leading nowhere is the shown-and-disabled affordance US-203 rejected.
    // The board draws four; US-1208 and US-1302 append the other two with their routes.
    expect(labels).toEqual(['Users', 'Models']);
    expect(labels).not.toContain('MCP servers');
    expect(labels).not.toContain('Reports');
  });

  it('becomes the pill strip below 768px', async () => {
    setMediaQuery(NARROW_VIEWPORT, true);
    await open('/admin/users');

    expect(element().querySelector('app-pill-subnav')).not.toBeNull();
    expect(element().querySelector('.admin-nav')).toBeNull();
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

  it('restores the previous tab on back (US-1209)', async () => {
    await open('/admin/users');
    await open('/admin/models');

    TestBed.inject(Location).back();
    // A macrotask: the location mock notifies asynchronously, so `whenStable` alone runs
    // before the router has heard about it.
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
    backend
      .expectOne((request) => request.url === USERS_URL)
      .flush(userPage([directoryUserFixture()]));
    await harness.fixture.whenStable();

    // Tabs are routes, not local state, so this is the router's behaviour rather than
    // anything the layout implements.
    expect(TestBed.inject(Router).url).toBe('/admin/users');
  });
});
