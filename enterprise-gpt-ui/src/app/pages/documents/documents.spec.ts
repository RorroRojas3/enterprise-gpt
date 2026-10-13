import { Location } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  getAllByRole,
  getByRole,
  getByText,
  queryAllByRole,
  queryByRole,
} from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UserDocumentDto } from '@shared/models/api/document.model';
import { formatShortDate } from '@shared/utils/format/relative-time';
import { DOCUMENT_PAGE_SIZE } from '@state/documents/document-library-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { documentPage, userDocumentFixture } from '@testing/documents';
import { iconOf } from '@testing/overlays';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { Documents } from './documents';

const DOCUMENTS_URL = `${TEST_API_BASE_URL}/api/documents`;

/**
 * `SearchInput`'s 300 ms window plus slack for a loaded CI runner. Real timers: faking the clock
 * also stalls the scheduler `whenStable()` awaits.
 */
const DEBOUNCE_MS = 450;

describe('Documents', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let clicks: HTMLAnchorElement[];

  beforeEach(async () => {
    clicks = [];

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

    // jsdom navigates on an anchor click and logs "Not implemented"; recording the
    // element is also the only way to assert what the browser was handed.
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicks.push(this);
    });
  });

  afterEach(() => {
    backend.verify();
    vi.restoreAllMocks();
  });

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function searchField(): HTMLInputElement {
    return getByRole<HTMLInputElement>(element(), 'searchbox', {
      name: 'Filter documents by name',
    });
  }

  /** Named "Loading…" while a page is on the wire, so either label finds it. */
  function loadMoreButton(): HTMLButtonElement | null {
    return queryByRole<HTMLButtonElement>(element(), 'button', { name: /^(Load more|Loading…)$/ });
  }

  /** The row names, read off each row's download button, the one control every row has. */
  function rowNames(): (string | undefined)[] {
    return queryAllByRole(element(), 'button', { name: /^Download / }).map((button) =>
      button.getAttribute('aria-label')?.replace(/^Download /, ''),
    );
  }

  /** One chevron per group header. */
  function groupToggles(): HTMLButtonElement[] {
    return queryAllByRole<HTMLButtonElement>(element(), 'button', { name: /^(Collapse|Expand) / });
  }

  /** The count the toolbar draws; aria-hidden, so it has no role to be found by. */
  function visibleCount(): HTMLElement | null {
    return element().querySelector('p[aria-hidden="true"]');
  }

  function emptyState(): HTMLElement {
    const empty = element().querySelector<HTMLElement>('app-empty-state');
    expect(empty).not.toBeNull();
    return empty!;
  }

  /** The unfiltered listing; refuses a request carrying `type=`, so a stale origin filter fails. */
  function expectListing(): TestRequest {
    return backend.expectOne(
      (request) => request.url === DOCUMENTS_URL && !request.params.has('type'),
    );
  }

  /** The request a screen with the assistant-created filter on issues. */
  function expectGeneratedListing(): TestRequest {
    return backend.expectOne(
      (request) => request.url === DOCUMENTS_URL && request.params.get('type') === 'generated',
    );
  }

  function url(): string {
    return TestBed.inject(Router).url;
  }

  /** Opens the screen and answers its first page with `items`. */
  async function open(
    route = '/documents',
    items: UserDocumentDto[] = [userDocumentFixture()],
  ): Promise<void> {
    await harness.navigateByUrl(route, Documents);
    expectListing().flush(documentPage(items, { pageSize: DOCUMENT_PAGE_SIZE }));
    await harness.fixture.whenStable();
  }

  /** Types into the field and waits out the debounce on real timers. */
  async function type(term: string): Promise<void> {
    const field = searchField();
    field.value = term;
    field.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();

    await new Promise((resolve) => setTimeout(resolve, DEBOUNCE_MS));
    await harness.fixture.whenStable();
  }

  /** Presses back and waits a macrotask, since the router handles the popstate asynchronously. */
  async function goBack(): Promise<void> {
    TestBed.inject(Location).back();
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
  }

  /** Rows from two conversations, so the grouped view has two groups to draw. */
  function twoConversations(): UserDocumentDto[] {
    return [
      userDocumentFixture({ name: 'alpha-plan.pdf', conversationName: 'Alpha' }),
      userDocumentFixture({ name: 'beta-notes.txt', conversationName: 'Beta' }),
    ];
  }

  it('issues exactly one request when opened', async () => {
    await open('/documents', twoConversations());

    // `afterEach`'s `verify()` is what makes this "exactly one".
    expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    expect(getByRole(element(), 'heading', { level: 1 }).textContent?.trim()).toBe('Documents');
  });

  it('renders each flat row: glyph, name, size, date, badge, and the conversation link', async () => {
    const doc = userDocumentFixture({
      name: 'q3-report.pdf',
      size: 2_411_724,
      conversationName: 'Quarterly review',
    });
    const other = userDocumentFixture({
      name: 'notes.txt',
      extension: '.txt',
      mimeType: 'text/plain',
      conversationName: 'Second conversation',
    });
    await open('/documents?view=flat', [doc, other]);

    const rows = [...element().querySelectorAll('app-document-row')];
    expect(rows).toHaveLength(2);

    const first = rows[0]! as HTMLElement;
    // The tone is the glyph's whole visual contract; there is no state to assert instead.
    const glyph = first.querySelector('app-icon')!;
    expect(iconOf(glyph)).toBe('bi-file-earmark-pdf');
    expect(glyph.classList.contains('text-danger')).toBe(true);
    expect(getByText(first, 'q3-report.pdf')).toBeTruthy();
    expect(getByText(first, '2.3 MB')).toBeTruthy();
    expect(getByText(first, formatShortDate(doc.dateCreated, true))).toBeTruthy();

    // The badge reads the row's own origin rather than a constant.
    for (const row of rows) {
      expect(row.querySelector('app-source-badge')?.textContent?.trim()).toBe('Uploaded');
    }

    const link = getByRole(first, 'link', { name: 'Quarterly review' });
    expect(link.getAttribute('href')).toBe(`/chat/${doc.conversationId}`);
  });

  it('mints the signed download link on click, and never on render', async () => {
    const doc = userDocumentFixture({ name: 'q3-report.pdf' });
    await open('/documents', [doc]);

    backend.expectNone((request) => request.url.includes('/api/documents/conversations/'));

    const button = getByRole<HTMLButtonElement>(element(), 'button', {
      name: 'Download q3-report.pdf',
    });
    button.click();
    await harness.fixture.whenStable();

    const request = backend.expectOne(
      `${TEST_API_BASE_URL}/api/documents/conversations/${doc.conversationId}/${doc.id}`,
    );
    expect(request.request.method).toBe('GET');
    expect(button.disabled).toBe(true);
    expect(button.getAttribute('aria-busy')).toBe('true');

    request.flush({
      downloadUrl: 'https://acct.blob.core.windows.net/documents/8f3/q3-report.pdf?sig=secret',
      fileName: 'q3-report.pdf',
      expiresAt: '2026-08-19T14:35:00+00:00',
    });
    await harness.fixture.whenStable();

    expect(clicks).toHaveLength(1);
    expect(clicks[0]?.download).toBe('q3-report.pdf');
    expect(button.disabled).toBe(false);
  });

  it('offers a retry on a failure, instead of the listing — and instead of the toolbar', async () => {
    await harness.navigateByUrl('/documents', Documents);
    expectListing().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    await harness.fixture.whenStable();

    const panel = element().querySelector<HTMLElement>('app-error-panel')!;
    expect(panel).not.toBeNull();
    expect(getByText(panel, 'Documents couldn’t load')).toBeTruthy();
    expect(getByText(panel, /Trace ID:/)).toBeTruthy();
    expect(queryByRole(element(), 'list')).toBeNull();
    expect(element().querySelector('app-search-input')).toBeNull();
    expect(queryByRole(element(), 'switch')).toBeNull();

    getByRole(panel, 'button', { name: 'Retry' }).click();
    await harness.fixture.whenStable();

    const retried = expectListing();
    expect(retried.request.params.get('skip')).toBe('0');
    retried.flush(documentPage([userDocumentFixture()], { pageSize: DOCUMENT_PAGE_SIZE }));
    await harness.fixture.whenStable();

    expect(element().querySelector('app-error-panel')).toBeNull();
    expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
  });

  describe('paging', () => {
    /** Opens with a full first page that leaves more behind. */
    async function openPaged(totalCount = 60): Promise<void> {
      await harness.navigateByUrl('/documents?view=flat', Documents);
      expectListing().flush(
        documentPage(
          Array.from({ length: DOCUMENT_PAGE_SIZE }, (_, index) =>
            userDocumentFixture({ name: `document-${index}.pdf` }),
          ),
          { totalCount, pageSize: DOCUMENT_PAGE_SIZE },
        ),
      );
      await harness.fixture.whenStable();
    }

    it('places one page of rows in the DOM, from one request', async () => {
      await openPaged(600);

      expect(element().querySelectorAll('app-document-row')).toHaveLength(DOCUMENT_PAGE_SIZE);
      backend.verify();
    });

    it('appends the next page in server order', async () => {
      await openPaged();

      loadMoreButton()!.click();
      await harness.fixture.whenStable();

      const request = expectListing();
      expect(request.request.params.get('skip')).toBe(String(DOCUMENT_PAGE_SIZE));
      request.flush(
        documentPage([userDocumentFixture({ name: 'document-25.pdf' })], {
          totalCount: 60,
          pageSize: DOCUMENT_PAGE_SIZE,
          skip: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      const names = rowNames();
      expect(names).toHaveLength(DOCUMENT_PAGE_SIZE + 1);
      expect(names.at(0)).toBe('document-0.pdf');
      expect(names.at(-1)).toBe('document-25.pdf');
    });

    it('removes the control rather than disabling it once everything is loaded', async () => {
      await openPaged(DOCUMENT_PAGE_SIZE);

      expect(loadMoreButton()).toBeNull();
    });

    it('marks the control busy while a page is on the wire', async () => {
      await openPaged();

      loadMoreButton()!.click();
      await harness.fixture.whenStable();
      expect(loadMoreButton()?.getAttribute('aria-disabled')).toBe('true');

      expectListing().flush(
        documentPage([], { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE, skip: 25 }),
      );
      await harness.fixture.whenStable();
    });

    it('announces the busy state through the region that already carries the count', async () => {
      await openPaged(312);

      const status = getByRole(element(), 'status');
      expect(status.textContent?.trim()).toBe('Showing 25 of 312 documents');

      loadMoreButton()!.click();
      await harness.fixture.whenStable();

      expect(status.textContent?.trim()).toBe('Showing 25 of 312 documents, loading more…');

      expectListing().flush(
        documentPage([], { totalCount: 312, pageSize: DOCUMENT_PAGE_SIZE, skip: 25 }),
      );
      await harness.fixture.whenStable();
    });

    it('fires the same request from the keyboard as from a click', async () => {
      await openPaged();

      const button = loadMoreButton()!;
      button.focus();
      // What Enter and Space do on a real `<button>`: the browser synthesises a click.
      button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      await harness.fixture.whenStable();

      const request = expectListing();
      expect(request.request.params.get('skip')).toBe(String(DOCUMENT_PAGE_SIZE));
      request.flush(documentPage([], { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE, skip: 25 }));
      await harness.fixture.whenStable();
    });

    it('moves focus to the last row when the control removes itself', async () => {
      await openPaged(DOCUMENT_PAGE_SIZE + 1);

      const button = loadMoreButton()!;
      button.focus();
      button.click();
      await harness.fixture.whenStable();

      expectListing().flush(
        documentPage([userDocumentFixture({ name: 'last.pdf' })], {
          totalCount: DOCUMENT_PAGE_SIZE + 1,
          pageSize: DOCUMENT_PAGE_SIZE,
          skip: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(loadMoreButton()).toBeNull();
      expect(document.activeElement?.getAttribute('aria-label')).toBe('Download last.pdf');
    });

    it('leaves focus on the control while there is still more to load', async () => {
      await openPaged(100);

      const button = loadMoreButton()!;
      button.focus();
      button.click();
      await harness.fixture.whenStable();

      expectListing().flush(
        documentPage([userDocumentFixture()], {
          totalCount: 100,
          pageSize: DOCUMENT_PAGE_SIZE,
          skip: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(document.activeElement).toBe(loadMoreButton());
    });

    it('leaves focus where the reader put it while the page was on the wire', async () => {
      await openPaged(DOCUMENT_PAGE_SIZE + 1);

      loadMoreButton()!.focus();
      loadMoreButton()!.click();
      await harness.fixture.whenStable();

      const field = searchField();
      field.focus();

      expectListing().flush(
        documentPage([userDocumentFixture({ name: 'last.pdf' })], {
          totalCount: DOCUMENT_PAGE_SIZE + 1,
          pageSize: DOCUMENT_PAGE_SIZE,
          skip: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(document.activeElement).toBe(field);
    });
  });

  describe('filtering by name', () => {
    it('asks the server, so a match past the loaded window still comes back', async () => {
      await open('/documents', twoConversations());

      await type('alpha');

      expect(url()).toBe('/documents?name=alpha');
      const request = expectListing();
      expect(request.request.params.get('name')).toBe('alpha');
      request.flush(
        documentPage([userDocumentFixture({ name: 'alpha-plan.pdf' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(rowNames()).toEqual(['alpha-plan.pdf']);
    });

    it('replaces history while refining, and pushes at on and off', async () => {
      await open('/documents', twoConversations());

      await type('alp');
      expectListing().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();

      await type('alpha');
      expectListing().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();
      expect(url()).toBe('/documents?name=alpha');

      // One press, not two: refining replaced rather than pushed.
      await goBack();
      expect(url()).toBe('/documents');
      expectListing().flush(documentPage(twoConversations(), { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });

    it('removes the key when the term is cleared', async () => {
      await open('/documents?name=alpha', [
        userDocumentFixture({ name: 'alpha-plan.pdf', conversationName: 'Alpha' }),
      ]);
      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);

      await type('');

      expect(url()).toBe('/documents');
      expectListing().flush(documentPage(twoConversations(), { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });

    it('filters a deep link on its one and only request', async () => {
      // One request because `bindQuery` gets a computation, not the constructor-time values.
      await harness.navigateByUrl('/documents?name=beta', Documents);

      const request = expectListing();
      expect(request.request.params.get('name')).toBe('beta');
      request.flush(
        documentPage([userDocumentFixture({ name: 'beta-notes.txt', conversationName: 'Beta' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(rowNames()).toEqual(['beta-notes.txt']);
      expect(searchField().value).toBe('beta');
    });
  });

  describe('the view toggle', () => {
    function groupSwitch(): HTMLInputElement {
      return getByRole<HTMLInputElement>(element(), 'switch', { name: 'Group by conversation' });
    }

    it('defaults to grouped, records only the deviation, and always pushes', async () => {
      await open('/documents', twoConversations());

      expect(groupSwitch().checked).toBe(true);
      expect(groupSwitch().getAttribute('role')).toBe('switch');
      expect(groupToggles()).toHaveLength(2);

      groupSwitch().click();
      await harness.fixture.whenStable();

      expect(url()).toBe('/documents?view=flat');
      expect(groupSwitch().checked).toBe(false);
      expect(groupToggles()).toHaveLength(0);

      groupSwitch().click();
      await harness.fixture.whenStable();

      // Toggling back removes the key rather than writing `view=grouped`.
      expect(url()).toBe('/documents');
      expect(groupToggles()).toHaveLength(2);

      // Both transitions pushed, so back walks through them.
      await goBack();
      expect(url()).toBe('/documents?view=flat');
      await goBack();
      expect(url()).toBe('/documents');
    });

    it('reads any value it never writes as grouped', async () => {
      await open('/documents?view=list', twoConversations());

      expect(groupSwitch().checked).toBe(true);
      expect(groupToggles()).toHaveLength(2);
    });

    it('lets the term and the view survive each other', async () => {
      await open('/documents?name=alpha', [
        userDocumentFixture({ name: 'alpha-plan.pdf', conversationName: 'Alpha' }),
      ]);

      groupSwitch().click();
      await harness.fixture.whenStable();

      // A view change is not a query change, so no request goes out and the rows stay.
      expect(url()).toContain('name=alpha');
      expect(url()).toContain('view=flat');
      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
    });
  });

  describe('the assistant-created filter', () => {
    function sourceSwitch(): HTMLInputElement {
      return getByRole<HTMLInputElement>(element(), 'switch', { name: 'Generated only' });
    }

    it('is off by default and sends no origin with the request', async () => {
      // `open` flushes through `expectListing`, which refuses a request carrying `type=`.
      await open('/documents', twoConversations());

      expect(sourceSwitch().checked).toBe(false);
      expect(sourceSwitch().getAttribute('role')).toBe('switch');
    });

    it('names the narrowed set in the count', async () => {
      await harness.navigateByUrl('/documents?source=generated', Documents);
      expectGeneratedListing().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(visibleCount()?.textContent?.trim()).toBe('Showing 1 of 1 generated document');
    });

    it('narrows the listing at the server, records only the deviation, and always pushes', async () => {
      await open('/documents', twoConversations());

      sourceSwitch().click();
      await harness.fixture.whenStable();

      expect(url()).toBe('/documents?source=generated');
      expectGeneratedListing().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(sourceSwitch().checked).toBe(true);
      const rows = [...element().querySelectorAll('app-document-row')];
      expect(rows).toHaveLength(1);
      expect(rows[0]!.querySelector('app-source-badge')?.textContent?.trim()).toBe('Generated');

      sourceSwitch().click();
      await harness.fixture.whenStable();

      // Toggling back removes the key rather than writing `source=uploaded`.
      expect(url()).toBe('/documents');
      expectListing().flush(documentPage(twoConversations(), { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();

      await goBack();
      expect(url()).toBe('/documents?source=generated');
      expectGeneratedListing().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();
    });

    it('filters on first render from a deep link', async () => {
      await harness.navigateByUrl('/documents?source=generated', Documents);
      expectGeneratedListing().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(sourceSwitch().checked).toBe(true);
      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
    });

    it('reads any value it never writes as unfiltered', async () => {
      await open('/documents?source=uploaded', twoConversations());

      expect(sourceSwitch().checked).toBe(false);
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });

    it('explains an empty filtered set and offers the whole library back', async () => {
      await harness.navigateByUrl('/documents?source=generated', Documents);
      expectGeneratedListing().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();

      const empty = emptyState();
      expect(empty.textContent).toContain('No generated documents yet');
      expect(empty.textContent).not.toContain('Nothing uploaded yet');

      getByRole(empty, 'button', { name: 'Show every document' }).click();
      await harness.fixture.whenStable();

      // The button went with its branch.
      expect(document.activeElement).toBe(sourceSwitch());
      expect(url()).toBe('/documents');
      expectListing().flush(documentPage(twoConversations(), { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });

    it('reports a term with no matches as such, and clears only the term', async () => {
      await harness.navigateByUrl('/documents?source=generated&name=summary', Documents);
      expectGeneratedListing().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();

      expect(getByRole(emptyState(), 'heading').textContent?.trim()).toBe(
        'No documents match “summary”',
      );

      getByRole(emptyState(), 'button', { name: 'Clear filter' }).click();
      await harness.fixture.whenStable();

      expect(url()).toBe('/documents?source=generated');
      expectGeneratedListing().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();
      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
    });

    it('carries the origin with the term when only the term changes', async () => {
      await harness.navigateByUrl('/documents?source=generated', Documents);
      expectGeneratedListing().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      await type('summary');

      const request = expectGeneratedListing();
      expect(request.request.params.get('name')).toBe('summary');
      request.flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();

      expect(url()).toContain('source=generated');
    });

    it('lets the term and the origin survive each other', async () => {
      await open('/documents?name=summary', twoConversations());

      sourceSwitch().click();
      await harness.fixture.whenStable();

      expect(url()).toContain('name=summary');
      expect(url()).toContain('source=generated');
      const request = expectGeneratedListing();
      expect(request.request.params.get('name')).toBe('summary');
      request.flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
    });
  });

  describe('the grouped view', () => {
    it('draws a linking header with a count, and rows without the conversation column', async () => {
      const alpha = userDocumentFixture({
        name: 'alpha-plan.pdf',
        conversationName: 'Alpha',
        conversationDocumentCount: 2,
      });
      const beta = userDocumentFixture({ name: 'beta-notes.txt', conversationName: 'Beta' });
      const alphaOlder = userDocumentFixture({
        name: 'alpha-summary.md',
        conversationId: alpha.conversationId,
        conversationName: 'Alpha',
        conversationDocumentCount: 2,
      });
      await open('/documents', [alpha, beta, alphaOlder]);

      expect(groupToggles()).toHaveLength(2);

      // The headers' links are the only ones: grouped rows drop their conversation link.
      const links = getAllByRole(element(), 'link');
      expect(links.map((link) => link.textContent?.trim())).toEqual(['Alpha', 'Beta']);
      expect(links[0]?.getAttribute('href')).toBe(`/chat/${alpha.conversationId}`);
      expect(getByText(element(), '2 files')).toBeTruthy();
      expect(getByText(element(), '1 file')).toBeTruthy();

      expect(element().querySelectorAll('app-source-badge')).toHaveLength(3);
    });

    it('says how many of a conversation’s files a partially loaded group holds', async () => {
      const alpha = userDocumentFixture({
        name: 'alpha-plan.pdf',
        conversationName: 'Alpha',
        conversationDocumentCount: 8,
      });
      await open('/documents', [alpha]);

      expect(getByText(element(), '1 of 8 files')).toBeTruthy();
    });

    it('updates a group’s count as Load more appends rows to it', async () => {
      const alpha = userDocumentFixture({
        name: 'alpha-plan.pdf',
        conversationName: 'Alpha',
        conversationDocumentCount: 8,
      });
      await harness.navigateByUrl('/documents', Documents);
      expectListing().flush(
        documentPage([alpha], { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE }),
      );
      await harness.fixture.whenStable();

      loadMoreButton()!.click();
      await harness.fixture.whenStable();

      expectListing().flush(
        documentPage(
          [
            userDocumentFixture({
              name: 'alpha-summary.md',
              conversationId: alpha.conversationId,
              conversationName: 'Alpha',
              conversationDocumentCount: 8,
            }),
          ],
          { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE, skip: 1 },
        ),
      );
      await harness.fixture.whenStable();

      expect(getByText(element(), '2 of 8 files')).toBeTruthy();
      expect(groupToggles()).toHaveLength(1);
    });

    it('collapses a group from its chevron, removing its rows from the DOM', async () => {
      await open('/documents', twoConversations());
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);

      const chevron = getByRole(element(), 'button', { name: 'Collapse Alpha' });
      expect(chevron.getAttribute('aria-expanded')).toBe('true');
      expect(chevron.getAttribute('aria-controls')).not.toBeNull();

      chevron.click();
      await harness.fixture.whenStable();

      expect(chevron.getAttribute('aria-expanded')).toBe('false');
      expect(chevron.getAttribute('aria-label')).toBe('Expand Alpha');
      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
      expect(groupToggles()).toHaveLength(2);

      chevron.click();
      await harness.fixture.whenStable();

      expect(chevron.getAttribute('aria-expanded')).toBe('true');
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });
  });

  describe('the empty states', () => {
    it('renders the never-uploaded state, with its exact copy and no action', async () => {
      await open('/documents', []);

      const empty = emptyState();
      expect(empty.querySelector('app-ridgeline')).not.toBeNull();
      expect(getByRole(empty, 'heading').textContent?.trim()).toBe('Nothing uploaded yet');
      expect(
        getByText(empty, 'Attach files in any conversation and they’ll appear here.'),
      ).toBeTruthy();
      expect(queryByRole(empty, 'button')).toBeNull();
      expect(queryByRole(empty, 'link')).toBeNull();
    });

    it('names the term on a zero-result deep link rather than the empty library', async () => {
      await harness.navigateByUrl('/documents?name=ghost', Documents);
      expectListing().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();

      expect(getByRole(emptyState(), 'heading').textContent?.trim()).toBe(
        'No documents match “ghost”',
      );
    });

    it('offers the no-matches state, naming the term, and clears back out of it', async () => {
      await open('/documents', twoConversations());

      await type('zzz');
      expectListing().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();

      const empty = emptyState();
      expect(getByRole(empty, 'heading').textContent?.trim()).toBe('No documents match “zzz”');
      expect(getByText(empty, 'Filters cover file names only.')).toBeTruthy();

      getByRole(empty, 'button', { name: 'Clear filter' }).click();
      await harness.fixture.whenStable();

      expect(url()).toBe('/documents');
      expectListing().flush(documentPage(twoConversations(), { pageSize: DOCUMENT_PAGE_SIZE }));
      await harness.fixture.whenStable();
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
      // The button went with the empty state.
      expect(document.activeElement).toBe(searchField());
    });
  });

  describe('the count summary', () => {
    it('shows the count aria-hidden and announces it once through the status region', async () => {
      await harness.navigateByUrl('/documents', Documents);
      expectListing().flush(
        documentPage(twoConversations(), { totalCount: 312, pageSize: DOCUMENT_PAGE_SIZE }),
      );
      await harness.fixture.whenStable();

      const visible = visibleCount();
      expect(visible?.textContent?.trim()).toBe('Showing 2 of 312 documents');
      expect(visible?.getAttribute('aria-hidden')).toBe('true');

      expect(getByRole(element(), 'status').textContent?.trim()).toBe('Showing 2 of 312 documents');
    });

    it('withholds the count while the first page loads', async () => {
      await harness.navigateByUrl('/documents', Documents);

      expect(visibleCount()).toBeNull();
      expect(getByRole(element(), 'status').textContent?.trim()).toBe('');

      expectListing().flush(
        documentPage([userDocumentFixture()], { pageSize: DOCUMENT_PAGE_SIZE }),
      );
      await harness.fixture.whenStable();

      expect(visibleCount()?.textContent?.trim()).toBe('Showing 1 of 1 document');
    });
  });
});
