import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { documentPage, userDocumentFixture } from '@testing/documents';
import { afterEach, beforeEach, describe, it } from 'vitest';
import { Documents } from './documents';

/**
 * The documents library route US-1405's first criterion names, in both themes and in
 * both of US-1003's view modes — the grouped default and `?view=flat` — because the
 * two render different anatomies: group headers with chevrons and links on one side,
 * the conversation link column on the other.
 *
 * These run in Chromium through the `test-a11y` target — see `src/testing/a11y.ts` for
 * why jsdom cannot host them. The screen is rendered with rows from two conversations
 * rather than empty, because an empty listing has no glyphs, no badges, no links and no
 * download controls in it — which is most of the surface worth auditing.
 */

const DOCUMENTS_URL = `${TEST_API_BASE_URL}/api/documents`;

describe('documents accessibility (US-1405)', () => {
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
        provideRouter([{ path: 'documents', component: Documents }], withComponentInputBinding()),
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

  /** Renders the screen into the document, where axe can read computed style. */
  async function open(url: string, theme: 'light' | 'dark'): Promise<HTMLElement> {
    applyTheme(theme);
    await harness.navigateByUrl(url);

    const element = harness.routeNativeElement as HTMLElement;
    document.body.append(element);
    attached = element;
    return element;
  }

  /** Rows from two conversations, so the grouped default has two groups to draw. */
  function flushListing(): void {
    backend
      .expectOne((request) => request.url === DOCUMENTS_URL)
      .flush(
        documentPage([
          userDocumentFixture({ name: 'q3-report.pdf', conversationName: 'Quarterly review' }),
          userDocumentFixture({
            name: 'migration-plan.docx',
            extension: '.docx',
            mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
            conversationName: 'Migration plan',
          }),
        ]),
      );
  }

  for (const theme of THEMES) {
    it(`finds nothing serious on the documents library in the ${theme} theme`, async () => {
      const element = await open('/documents', theme);

      flushListing();
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/documents (${theme})`);
    });

    it(`finds nothing serious on the flat listing in the ${theme} theme`, async () => {
      const element = await open('/documents?view=flat', theme);

      flushListing();
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/documents?view=flat (${theme})`);
    });
  }
});
