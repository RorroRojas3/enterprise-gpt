import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { McpServerActionsStore } from '@core/catalog/mcp-server-actions-store';
import { ModelActionsStore } from '@core/catalog/model-actions-store';
import { SessionStore } from '@core/session/session-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import {
  THEMES,
  VIEWPORTS,
  ViewportName,
  applyTheme,
  atViewport,
  clearTheme,
  expectNoHorizontalOverflow,
  expectNoSeriousViolations,
} from '@testing/a11y';
import { mcpServerFixture, modelFixture } from '@testing/catalog';
import { emptyUsageReportFixture, usageReportFixture } from '@testing/reports';
import { directoryUserFixture, flushPermissions, userPage } from '@testing/users';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
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
const REPORTS_URL = `${TEST_API_BASE_URL}/api/reports/usage`;

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

  afterEach(async () => {
    // Or the next file inherits whatever width the last test left behind.
    await atViewport('desktop');
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
        .flush([
          modelFixture({ name: 'gpt-4o' }),
          modelFixture({ name: 'o3-mini' }),
          // The summarizer's own shape: active, hidden from the picker. Its status dot is a
          // third state the other two rows never render.
          modelFixture({ name: 'rr-gpt5.6-luna', isUserSelectable: false }),
        ]);
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/models (${theme})`);
    });

    /**
     * The model form open (US-1207/US-104, frame `5f`).
     *
     * Eight labelled inputs, a select and four switches — none of which the table behind it
     * exercises. Opened on a hidden, **non**-default model deliberately: a hidden *default*
     * would render the conflict refusal, and `.model-form__error` is `--fail` on `--surface`,
     * which is 4.39:1 in the dark theme and so fails AA for normal text. That is an app-wide
     * token defect (every dialog's inline error sits on `--surface`), not a property of this
     * form, and auditing it from here would fail this test for a reason it does not own.
     */
    it(`finds nothing serious with the model form open in the ${theme} theme`, async () => {
      const element = await open('/admin/models', theme);

      backend
        .expectOne((request) => request.url === MODELS_URL)
        .flush([modelFixture({ name: 'gpt-4o', isDefault: true })]);
      await harness.fixture.whenStable();

      TestBed.inject(ModelActionsStore).beginEdit(
        modelFixture({ name: 'gpt-4o', isDefault: false, isUserSelectable: false }),
      );
      await harness.fixture.whenStable();

      expect(element.querySelector('#model-form-selectable')).not.toBeNull();
      expect(element.querySelector('#model-form-default-errors')).toBeNull();
      await expectNoSeriousViolations(element, `/admin/models form (${theme})`);
    });

    it(`finds nothing serious on the MCP registry in the ${theme} theme`, async () => {
      const element = await open('/admin/mcps', theme);

      backend
        .expectOne((request) => request.url === MCPS_URL)
        .flush([
          mcpServerFixture({ name: 'Andes Test MCP' }),
          mcpServerFixture({ name: 'Microsoft Learn', iconKey: 'microsoft' }),
        ]);
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/mcps (${theme})`);
    });

    /**
     * The MCP form open (US-1208/US-1210, frame `5h`).
     *
     * Six labelled controls, two of them selects, plus a `role="alert"` region per field
     * and a `role="status"` note — none of which the table behind it exercises. Opened on
     * an **edit** of a row whose icon key this build ships no artwork for, because that is
     * the one state that renders the note and the icon preview at once.
     */
    it(`finds nothing serious with the MCP form open in the ${theme} theme`, async () => {
      const element = await open('/admin/mcps', theme);

      backend
        .expectOne((request) => request.url === MCPS_URL)
        .flush([mcpServerFixture({ name: 'Microsoft Learn', iconKey: 'microsoft' })]);
      await harness.fixture.whenStable();

      TestBed.inject(McpServerActionsStore).beginEdit(
        mcpServerFixture({ name: 'Something New', iconKey: 'future-brand' }),
      );
      await harness.fixture.whenStable();

      expect(element.querySelector('#mcp-form-icon')).not.toBeNull();
      await expectNoSeriousViolations(element, `/admin/mcps form (${theme})`);
    });

    it(`finds nothing serious on the reports tab in the ${theme} theme`, async () => {
      // A real report rather than an empty screen. Every mark on this tab is drawn from the
      // response — the ridgeline, ten bars, three donut arcs and a paged table — and an audit of
      // the empty state would be an audit of an empty state.
      const element = await open('/admin/reports', theme);
      backend.expectOne((request) => request.url === REPORTS_URL).flush(usageReportFixture());
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/reports (${theme})`);
    });

    it(`finds nothing serious on an empty reports range in the ${theme} theme`, async () => {
      // The other half: a named empty state is what this tab shows for a quiet range, and it is
      // a different tree from the dashboard rather than the same one with zeroes in it.
      const element = await open('/admin/reports', theme);
      backend.expectOne((request) => request.url === REPORTS_URL).flush(emptyUsageReportFixture());
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/reports empty (${theme})`);
    });
  }

  // US-1403's criterion 5, which has been vacuous for two phases: "the reports charts stack into
  // a single column below 1024px" had nothing to assert while this tab was an unavailable panel.
  // A real browser is the only thing that can answer it, and overflow is the failure it produces —
  // a 640-unit chart, a 1.2fr/1fr split and a seven-column table all have to give way.
  for (const width of Object.keys(VIEWPORTS) as ViewportName[]) {
    it(`keeps the dashboard inside the viewport at the ${width} width`, async () => {
      await atViewport(width);
      const element = await open('/admin/reports', 'light');
      backend.expectOne((request) => request.url === REPORTS_URL).flush(usageReportFixture());
      await harness.fixture.whenStable();

      expectNoHorizontalOverflow(element, `/admin/reports ${width}`);
    });
  }
});
