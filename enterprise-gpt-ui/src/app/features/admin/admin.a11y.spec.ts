import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from '@core/session/session-store';
import { McpServerActionsStore } from '@state/mcp-server-actions-store';
import { ModelActionsStore } from '@state/model-actions-store';
import {
  THEMES,
  VIEWPORTS,
  ViewportName,
  applyTheme,
  atViewport,
  clearTheme,
  expectNoHorizontalOverflow,
  expectNoSeriousViolations,
  expectScrollsToItsLastRow,
  mountLikeShell,
} from '@testing/a11y';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpServerFixture, modelFixture } from '@testing/catalog';
import { emptyUsageReportFixture, usageReportFixture } from '@testing/reports';
import { UPLOAD_FILE_GRANT } from '@testing/session';
import { directoryUserFixture, flushPermissions, userPage } from '@testing/users';
import { adminRoutes } from './admin.routes';

/**
 * Audits the assembled admin routes, with real stores and data, in Chromium via `test-a11y`
 * (see `src/testing/a11y.ts`); an isolated, empty component would hide almost every defect.
 */

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;
const MODELS_URL = `${TEST_API_BASE_URL}/api/models/all`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps/all`;
const REPORTS_URL = `${TEST_API_BASE_URL}/api/reports/usage`;

@Component({ template: 'chat' })
class ChatStub {}

describe('administration accessibility', () => {
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

      // With rows, so the permission filter has options to audit rather than a lone "any".
      flushPermissions(backend);

      // Two rows rather than none: an empty table has no cells, row controls or badges to audit.
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

    // A hidden default is the one state that renders field feedback without a request.
    it(`finds nothing serious on the model form's field feedback in the ${theme} theme`, async () => {
      const element = await open('/admin/models', theme);

      backend
        .expectOne((request) => request.url === MODELS_URL)
        .flush([modelFixture({ name: 'gpt-4o', isDefault: true })]);
      await harness.fixture.whenStable();

      TestBed.inject(ModelActionsStore).beginEdit(
        modelFixture({ name: 'gpt-4o', isDefault: true, isUserSelectable: false }),
      );
      await harness.fixture.whenStable();

      expect(element.querySelector('#model-form-default-errors')).not.toBeNull();
      await expectNoSeriousViolations(element, `/admin/models form feedback (${theme})`);
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
     * Opened on an edit whose icon key has no artwork: the one state that renders the note and
     * the icon preview at once.
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
      // A real report: every mark on this tab is drawn from the response.
      const element = await open('/admin/reports', theme);
      backend.expectOne((request) => request.url === REPORTS_URL).flush(usageReportFixture());
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/reports (${theme})`);
    });

    it(`finds nothing serious on an empty reports range in the ${theme} theme`, async () => {
      // The empty state is a different tree, not the dashboard with zeroes in it.
      const element = await open('/admin/reports', theme);
      backend.expectOne((request) => request.url === REPORTS_URL).flush(emptyUsageReportFixture());
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/admin/reports empty (${theme})`);
    });
  }

  /**
   * The sidebar's width at each viewport; auditing against the bare viewport would give the
   * fitting tests room production does not have.
   */
  const SIDEBAR_TRACK: Record<ViewportName, number> = {
    desktop: 260,
    desktopMin: 260,
    tablet: 60,
    mobile: 0,
  };

  /** {@link open}, narrowed to the width the shell would actually leave. */
  async function openInShell(url: string, width: ViewportName): Promise<HTMLElement> {
    await atViewport(width);
    const element = await open(url, 'light');
    element.style.width = `calc(100% - ${SIDEBAR_TRACK[width]}px)`;
    return element;
  }

  // `DataTable` restructures twice (full, minus `hideOnTablet` columns, cards), so each list is
  // flushed with the widest values it can hold and checked at every width.
  for (const width of Object.keys(VIEWPORTS) as ViewportName[]) {
    it(`keeps the user directory inside the viewport at the ${width} width`, async () => {
      const element = await openInShell('/admin/users', width);

      flushPermissions(backend);
      backend
        .expectOne((request) => request.url === USERS_URL)
        .flush(
          userPage([
            directoryUserFixture({ firstName: 'Ada', lastName: 'Lovelace' }),
            directoryUserFixture({
              firstName: 'Grace',
              lastName: 'Hopper',
              email: 'grace.hopper_a_very_long_guest_address#EXT#@contoso.onmicrosoft.com',
              // The badge's whole reason for shrinking: this name is administrator-authored
              // and the column that holds it is not.
              permissions: [
                {
                  ...UPLOAD_FILE_GRANT,
                  name: 'A deliberately long administrator-authored permission name',
                },
              ],
            }),
          ]),
        );
      await harness.fixture.whenStable();

      expectNoHorizontalOverflow(element, `/admin/users ${width}`);
    });

    it(`keeps the model catalog inside the viewport at the ${width} width`, async () => {
      const element = await openInShell('/admin/models', width);

      backend
        .expectOne((request) => request.url === MODELS_URL)
        .flush([
          modelFixture({ name: 'gpt-4o' }),
          modelFixture({
            name: 'A deliberately long administrator-authored model name',
            deploymentName: 'contoso-eastus2-gpt-5-6-luna-provisioned-deployment',
          }),
        ]);
      await harness.fixture.whenStable();

      expectNoHorizontalOverflow(element, `/admin/models ${width}`);
    });

    it(`keeps the MCP registry inside the viewport at the ${width} width`, async () => {
      const element = await openInShell('/admin/mcps', width);

      backend
        .expectOne((request) => request.url === MCPS_URL)
        .flush([
          mcpServerFixture({ name: 'Andes Test MCP' }),
          mcpServerFixture({
            name: 'Azure DevOps',
            url: 'https://mcp.dev.azure.com/a-long-organisation-name/_apis/mcp/v1/endpoint',
            scope: 'https://mcp.dev.azure.com/a-long-organisation-name/.default',
          }),
        ]);
      await harness.fixture.whenStable();

      expectNoHorizontalOverflow(element, `/admin/mcps ${width}`);
    });
  }

  // The layout scrolls itself, since the shell's main column clips; a host that grew with its
  // content instead would strand the last rows below the fold.
  for (const width of Object.keys(VIEWPORTS) as ViewportName[]) {
    it(`scrolls the user directory to its last row at the ${width} width`, async () => {
      await atViewport(width);
      applyTheme('light');
      await harness.navigateByUrl('/admin/users');

      const element = harness.routeNativeElement as HTMLElement;
      attached = mountLikeShell(element);
      flushPermissions(backend);
      backend
        .expectOne((request) => request.url === USERS_URL)
        .flush(userPage(Array.from({ length: 25 }, () => directoryUserFixture())));
      await harness.fixture.whenStable();

      const rows = element.querySelectorAll<HTMLElement>('tbody tr, app-card-row');
      expectScrollsToItsLastRow(element, rows[rows.length - 1]!, `/admin/users (${width})`);
    });
  }

  // The charts must stack below 992px; overflow is the failure, since a 640-unit chart, a
  // 1.2fr/1fr split and a seven-column table all have to give way.
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
