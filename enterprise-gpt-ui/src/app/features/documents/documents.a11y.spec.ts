import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  THEMES,
  VIEWPORTS,
  applyTheme,
  atViewport,
  clearTheme,
  expectNoSeriousViolations,
  expectScrollsToItsLastRow,
  mountLikeShell,
} from '@testing/a11y';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { documentPage, userDocumentFixture } from '@testing/documents';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DOCUMENT_PAGE_SIZE } from './document-library-store';
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

  /**
   * Rows from two conversations, so the grouped default has two groups to draw — and one
   * of each origin, so both source badges are measured rather than only the muted one.
   */
  function flushListing(): void {
    backend
      .expectOne((request) => request.url === DOCUMENTS_URL)
      .flush(
        documentPage(
          [
            userDocumentFixture({ name: 'q3-report.pdf', conversationName: 'Quarterly review' }),
            userDocumentFixture({
              name: 'migration-plan.docx',
              extension: '.docx',
              mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
              type: 'Generated',
              conversationName: 'Migration plan',
            }),
          ],
          { pageSize: DOCUMENT_PAGE_SIZE },
        ),
      );
  }

  /** A full first page with more behind it, so the Load more control renders. */
  function flushPagedListing(): void {
    backend
      .expectOne((request) => request.url === DOCUMENTS_URL)
      .flush(
        documentPage(
          Array.from({ length: DOCUMENT_PAGE_SIZE }, (_, index) =>
            userDocumentFixture({
              name: `document-${index}.pdf`,
              conversationName: 'One conversation',
            }),
          ),
          { totalCount: 312, pageSize: DOCUMENT_PAGE_SIZE },
        ),
      );
  }

  /** More rows than any viewport this app supports can show at once. */
  function manyDocuments(count: number): ReturnType<typeof userDocumentFixture>[] {
    return Array.from({ length: count }, (_, index) =>
      userDocumentFixture({ name: `document-${index}.pdf`, conversationName: 'One conversation' }),
    );
  }

  /** The filtered screen with nothing to show — the one empty state with an action in it. */
  function flushNoGeneratedDocuments(): void {
    backend
      .expectOne(
        (request) => request.url === DOCUMENTS_URL && request.params.get('type') === 'generated',
      )
      .flush(documentPage([]));
  }

  for (const viewport of Object.keys(VIEWPORTS) as (keyof typeof VIEWPORTS)[]) {
    it(`scrolls to its last row at the ${viewport} width`, async () => {
      await atViewport(viewport);
      applyTheme('light');
      await harness.navigateByUrl('/documents?view=flat');

      const element = harness.routeNativeElement as HTMLElement;
      attached = mountLikeShell(element);
      backend
        .expectOne((request) => request.url === DOCUMENTS_URL)
        .flush(documentPage(manyDocuments(40), { pageSize: 40 }));
      await harness.fixture.whenStable();

      const rows = element.querySelectorAll<HTMLElement>('app-document-row');
      expectScrollsToItsLastRow(element, rows[rows.length - 1]!, `/documents (${viewport})`);
    });
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

    it(`finds nothing serious on the empty generated listing in the ${theme} theme`, async () => {
      const element = await open('/documents?source=generated', theme);

      flushNoGeneratedDocuments();
      await harness.fixture.whenStable();

      await expectNoSeriousViolations(element, `/documents?source=generated (${theme})`);
    });

    it(`finds nothing serious with the Load more control present in the ${theme} theme`, async () => {
      const element = await open('/documents', theme);

      flushPagedListing();
      await harness.fixture.whenStable();

      expect(element.querySelector('.documents__more button')).not.toBeNull();

      await expectNoSeriousViolations(element, `/documents with Load more (${theme})`);
    });

    it(`finds nothing serious with the Load more control busy in the ${theme} theme`, async () => {
      const element = await open('/documents', theme);

      flushPagedListing();
      await harness.fixture.whenStable();

      element.querySelector<HTMLButtonElement>('.documents__more button')?.click();
      await harness.fixture.whenStable();

      // `aria-disabled`, never the native attribute: a disabled button would drop focus
      // mid-press, which is the state this case exists to audit.
      expect(element.querySelector('.documents__more button')?.getAttribute('aria-disabled')).toBe(
        'true',
      );

      await expectNoSeriousViolations(element, `/documents loading more (${theme})`);

      backend
        .expectOne((request) => request.url === DOCUMENTS_URL)
        .flush(
          documentPage([], {
            totalCount: 312,
            pageSize: DOCUMENT_PAGE_SIZE,
            skip: DOCUMENT_PAGE_SIZE,
          }),
        );
      await harness.fixture.whenStable();
    });
  }
});
