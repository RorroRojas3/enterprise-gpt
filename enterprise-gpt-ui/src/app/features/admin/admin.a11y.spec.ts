import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { SessionStore } from '@core/session/session-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { mcpServerFixture, modelFixture } from '@testing/catalog';
import { directoryUserFixture, flushPermissions, userPage } from '@testing/users';
import { afterEach, beforeEach, describe, it } from 'vitest';
import { adminRoutes } from './admin.routes';

/**
 * The four administration routes US-1405's first criterion names, in both themes.
 *
 * These run in Chromium through the `test-a11y` target — see `src/testing/a11y.ts` for
 * why jsdom cannot host them. The harness is `admin-layout.spec.ts`'s, deliberately: the
 * point is to audit the screens as they are actually assembled, with their real routes,
 * their real stores and real data in them, rather than a component in isolation with
 * empty state, which is the configuration in which almost nothing is wrong.
 */

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;
const MODELS_URL = `${TEST_API_BASE_URL}/api/models/all`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps/all`;

@Component({ template: 'chat' })
class ChatStub {}

describe('administration accessibility (US-1405)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let attached: HTMLElement | null = null;

  beforeEach(async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SessionStore, useValue: { user: signal(null) } },
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
    attached?.remove();
    attached = null;
    clearTheme();
    backend.verify();
  });

  /** Renders one tab into the document, where axe can read computed style. */
  async function open(url: string, theme: 'light' | 'dark'): Promise<HTMLElement> {
    applyTheme(theme);
    await harness.navigateByUrl(url);

    const element = harness.routeNativeElement as HTMLElement;
    document.body.append(element);
    attached = element;
    return element;
  }

  for (const theme of THEMES) {
    it(`finds nothing serious on the user directory in the ${theme} theme`, async () => {
      const element = await open('/admin/users', theme);

      // Flushed with rows so the permission filter has options to audit rather than the
      // single "any" it renders before the catalog lands (US-1206).
      flushPermissions(backend);

      // Two rows rather than none: an empty table has no cells, no per-row controls and
      // no permission badges, which is most of the surface worth auditing.
      backend
        .expectOne((request) => request.url === USERS_URL)
        .flush(
          userPage([
            directoryUserFixture({ firstName: 'Ada', lastName: 'Lovelace' }),
            directoryUserFixture({ firstName: 'Grace', lastName: 'Hopper' }),
          ]),
        );
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/users (${theme})`);
    });

    it(`finds nothing serious on the model catalog in the ${theme} theme`, async () => {
      const element = await open('/admin/models', theme);

      backend
        .expectOne((request) => request.url === MODELS_URL)
        .flush([modelFixture({ name: 'gpt-4o' }), modelFixture({ name: 'o3-mini' })]);
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/models (${theme})`);
    });

    it(`finds nothing serious on the MCP registry in the ${theme} theme`, async () => {
      const element = await open('/admin/mcps', theme);

      backend
        .expectOne((request) => request.url === MCPS_URL)
        .flush([mcpServerFixture({ name: 'Andes Test MCP' })]);
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/mcps (${theme})`);
    });

    it(`finds nothing serious on the reports tab in the ${theme} theme`, async () => {
      // No store and no request by design (US-1209): this tab is the shared
      // `UnavailablePanel` until US-1301 exists to feed it.
      const element = await open('/admin/reports', theme);
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/reports (${theme})`);
    });
  }
});
