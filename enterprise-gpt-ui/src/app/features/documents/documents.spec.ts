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
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UserDocumentDto } from '@domain/api/document';
import { formatShortDate } from '@domain/format/relative-time';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { documentPage, userDocumentFixture } from '@testing/documents';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { DOCUMENT_LOAD_CEILING, DOCUMENT_PAGE_SIZE } from './document-library-store';
import { Documents } from './documents';

const DOCUMENTS_URL = `${TEST_API_BASE_URL}/api/documents`;

/**
 * `SearchInput`'s own 300 ms window, with room for the effect that follows it and
 * for a loaded CI runner — these are real timers, because faking the clock also
 * stalls the scheduler `whenStable()` awaits.
 */
const DEBOUNCE_MS = 450;

describe('Documents (US-1002, US-1003)', () => {
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

  /**
   * The unfiltered drain. It refuses a request carrying `type=`, so a regression that
   * kept the origin filter after the URL dropped it fails here rather than passing.
   */
  function expectDrain(): TestRequest {
    return backend.expectOne(
      (request) => request.url === DOCUMENTS_URL && !request.params.has('type'),
    );
  }

  /** The drain a screen with the assistant-created filter on issues. */
  function expectGeneratedDrain(): TestRequest {
    return backend.expectOne(
      (request) => request.url === DOCUMENTS_URL && request.params.get('type') === 'generated',
    );
  }

  function url(): string {
    return TestBed.inject(Router).url;
  }

  /** Opens the screen and answers its drain's first — and only — page with `items`. */
  async function open(
    route = '/documents',
    items: UserDocumentDto[] = [userDocumentFixture()],
  ): Promise<void> {
    await harness.navigateByUrl(route, Documents);
    expectDrain().flush(documentPage(items));
    await harness.fixture.whenStable();
  }

  /**
   * Types into the field and waits the debounce out with **real** timers: it is a
   * `setTimeout` inside an effect, and faking the clock also stalls the scheduler
   * `whenStable` awaits.
   */
  async function type(term: string): Promise<void> {
    const field = element().querySelector<HTMLInputElement>('.search__field');
    expect(field).not.toBeNull();
    field!.value = term;
    field!.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();

    await new Promise((resolve) => setTimeout(resolve, DEBOUNCE_MS));
    await harness.fixture.whenStable();
  }

  /**
   * Presses back and lets the router finish.
   *
   * `Location.back()` raises a popstate the router handles asynchronously, so
   * `whenStable()` alone would assert against the URL it had before the pop.
   */
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

  it('issues exactly one drain when opened', async () => {
    await open('/documents', twoConversations());

    // `afterEach`'s `verify()` is what makes this "exactly one".
    expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    expect(element().querySelector('h1')?.textContent?.trim()).toBe('Documents');
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

    const first = rows[0]!;
    expect(first.querySelector('.document-row__glyph--fail')).not.toBeNull();
    expect(first.querySelector('.document-row__name')?.textContent?.trim()).toBe('q3-report.pdf');
    expect(first.querySelector('.document-row__size')?.textContent?.trim()).toBe('2.3 MB');
    expect(first.querySelector('.document-row__date')?.textContent?.trim()).toBe(
      formatShortDate(doc.dateCreated, true),
    );

    // The badge reads the row's own origin rather than a constant.
    for (const row of rows) {
      expect(row.querySelector('app-source-badge')?.textContent?.trim()).toBe('Uploaded');
    }

    // The flat view's link column (US-1002's criterion).
    const link = first.querySelector<HTMLAnchorElement>('.document-row__conversation');
    expect(link?.textContent?.trim()).toBe('Quarterly review');
    expect(link?.getAttribute('href')).toBe(`/chat/${doc.conversationId}`);
  });

  it('mints the signed download link on click, and never on render', async () => {
    const doc = userDocumentFixture({ name: 'q3-report.pdf' });
    await open('/documents', [doc]);

    // Zero requests at render (US-804): a listed row must not put a live credential
    // in flight for a file the reader merely looked at.
    backend.expectNone((request) => request.url.includes('/api/documents/conversations/'));

    const button = element().querySelector<HTMLButtonElement>(
      '[aria-label="Download q3-report.pdf"]',
    );
    expect(button).not.toBeNull();
    button!.click();
    await harness.fixture.whenStable();

    const request = backend.expectOne(
      `${TEST_API_BASE_URL}/api/documents/conversations/${doc.conversationId}/${doc.id}`,
    );
    expect(request.request.method).toBe('GET');
    // Busy while the link is on the wire.
    expect(button!.disabled).toBe(true);
    expect(button!.querySelector('.kit-spinner')).not.toBeNull();

    request.flush({
      downloadUrl: 'https://acct.blob.core.windows.net/documents/8f3/q3-report.pdf?sig=secret',
      fileName: 'q3-report.pdf',
      expiresAt: '2026-08-19T14:35:00+00:00',
    });
    await harness.fixture.whenStable();

    expect(clicks).toHaveLength(1);
    expect(clicks[0]?.download).toBe('q3-report.pdf');
    expect(button!.disabled).toBe(false);
  });

  it('offers a retry on a failure, instead of the listing — and instead of the toolbar', async () => {
    await harness.navigateByUrl('/documents', Documents);
    expectDrain().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    await harness.fixture.whenStable();

    const panel = element().querySelector('app-error-panel');
    expect(panel).not.toBeNull();
    expect(panel?.querySelector('.error-panel__heading')?.textContent?.trim()).toBe(
      'Documents couldn’t load',
    );
    expect(panel?.querySelector('.error-panel__trace')?.textContent).toContain('Trace ID:');
    expect(element().querySelector('.documents__list')).toBeNull();
    // Frame 4k draws the error card alone: a client-side filter over a failed load
    // narrows nothing, so the toolbar goes too — unlike the conversations library,
    // whose field drives a server query.
    expect(element().querySelector('app-search-input')).toBeNull();
    expect(element().querySelector('.documents__group-toggle')).toBeNull();

    panel?.querySelector<HTMLButtonElement>('button')?.click();
    await harness.fixture.whenStable();

    const retried = expectDrain();
    expect(retried.request.params.get('skip')).toBe('0');
    retried.flush(documentPage([userDocumentFixture()]));
    await harness.fixture.whenStable();

    expect(element().querySelector('app-error-panel')).toBeNull();
    expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
  });

  it('states the ceiling when the drain stopped short', async () => {
    await harness.navigateByUrl('/documents', Documents);

    for (let page = 0; page * DOCUMENT_PAGE_SIZE < DOCUMENT_LOAD_CEILING; page++) {
      expectDrain().flush(
        documentPage(
          Array.from({ length: DOCUMENT_PAGE_SIZE }, () => userDocumentFixture()),
          { totalCount: 600, skip: page * DOCUMENT_PAGE_SIZE },
        ),
      );
      await harness.fixture.whenStable();
    }

    expect(element().querySelectorAll('app-document-row')).toHaveLength(DOCUMENT_LOAD_CEILING);
    expect(element().querySelector('.documents__ceiling')?.textContent?.trim()).toBe(
      'Showing the 500 most recent of 600 documents.',
    );
  });

  describe('filtering by name (US-1003)', () => {
    it('narrows client-side: the URL gains the term and no request goes out', async () => {
      await open('/documents', twoConversations());

      await type('alpha');

      // The whole point of regime A's drain: the set is already here.
      backend.expectNone(() => true);
      expect(url()).toBe('/documents?name=alpha');
      const rows = [...element().querySelectorAll('app-document-row')];
      expect(rows).toHaveLength(1);
      expect(rows[0]?.querySelector('.document-row__name')?.textContent?.trim()).toBe(
        'alpha-plan.pdf',
      );
    });

    it('replaces history while refining, and pushes at on and off', async () => {
      await open('/documents', twoConversations());

      await type('alp');
      await type('alpha');
      expect(url()).toBe('/documents?name=alpha');

      // One press, not two: refining replaced rather than pushed, so back lands on
      // the unfiltered list — and restores its rows without a request.
      await goBack();
      expect(url()).toBe('/documents');
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });

    it('removes the key when the term is cleared', async () => {
      await open('/documents?name=alpha', twoConversations());
      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);

      await type('');

      expect(url()).toBe('/documents');
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });

    it('filters a deep link on its one and only drain', async () => {
      // Handing `bindQuery` the *signal* is what makes the first render filtered:
      // reading its value in the constructor would flash the whole set first.
      await open('/documents?name=beta', twoConversations());

      const rows = [...element().querySelectorAll('app-document-row')];
      expect(rows).toHaveLength(1);
      expect(rows[0]?.querySelector('.document-row__name')?.textContent?.trim()).toBe(
        'beta-notes.txt',
      );
      expect(element().querySelector<HTMLInputElement>('.search__field')?.value).toBe('beta');
    });
  });

  describe('the view toggle (US-1003)', () => {
    function groupSwitch(): HTMLInputElement {
      const input = element().querySelector<HTMLInputElement>('.documents__group-toggle input');
      expect(input).not.toBeNull();
      return input!;
    }

    it('defaults to grouped, records only the deviation, and always pushes', async () => {
      await open('/documents', twoConversations());

      // Frame 4j draws the switch checked; the bare URL is the grouped view.
      expect(groupSwitch().checked).toBe(true);
      expect(groupSwitch().getAttribute('role')).toBe('switch');
      expect(element().querySelectorAll('.documents__group-header')).toHaveLength(2);

      groupSwitch().click();
      await harness.fixture.whenStable();

      expect(url()).toBe('/documents?view=flat');
      expect(groupSwitch().checked).toBe(false);
      expect(element().querySelectorAll('.documents__group-header')).toHaveLength(0);

      groupSwitch().click();
      await harness.fixture.whenStable();

      // Toggling back removes the key rather than writing `view=grouped`.
      expect(url()).toBe('/documents');
      expect(element().querySelectorAll('.documents__group-header')).toHaveLength(2);

      // Both transitions pushed, so back walks through them.
      await goBack();
      expect(url()).toBe('/documents?view=flat');
      await goBack();
      expect(url()).toBe('/documents');
    });

    it('reads any value it never writes as grouped', async () => {
      await open('/documents?view=list', twoConversations());

      expect(groupSwitch().checked).toBe(true);
      expect(element().querySelectorAll('.documents__group-header')).toHaveLength(2);
    });

    it('lets the term and the view survive each other', async () => {
      await open('/documents?name=alpha', twoConversations());

      groupSwitch().click();
      await harness.fixture.whenStable();

      expect(url()).toContain('name=alpha');
      expect(url()).toContain('view=flat');
      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
    });
  });

  describe('the assistant-created filter', () => {
    function sourceSwitch(): HTMLInputElement {
      const input = element().querySelector<HTMLInputElement>('.documents__source-toggle input');
      expect(input).not.toBeNull();
      return input!;
    }

    it('is off by default and sends no origin with the drain', async () => {
      // `open` flushes through `expectDrain`, which refuses a request carrying `type=`.
      await open('/documents', twoConversations());

      expect(sourceSwitch().checked).toBe(false);
      expect(sourceSwitch().getAttribute('role')).toBe('switch');
    });

    it('names the narrowed set in the count', async () => {
      await harness.navigateByUrl('/documents?source=generated', Documents);
      expectGeneratedDrain().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })]),
      );
      await harness.fixture.whenStable();

      // "1 document" would describe the library rather than the set on screen.
      expect(element().querySelector('.documents__count')?.textContent?.trim()).toBe(
        '1 generated document',
      );
    });

    it('narrows the listing at the server, records only the deviation, and always pushes', async () => {
      await open('/documents', twoConversations());

      sourceSwitch().click();
      await harness.fixture.whenStable();

      expect(url()).toBe('/documents?source=generated');
      expectGeneratedDrain().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })]),
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
      expectDrain().flush(documentPage(twoConversations()));
      await harness.fixture.whenStable();

      await goBack();
      expect(url()).toBe('/documents?source=generated');
      expectGeneratedDrain().flush(documentPage([]));
      await harness.fixture.whenStable();
    });

    it('filters on first render from a deep link', async () => {
      await harness.navigateByUrl('/documents?source=generated', Documents);
      expectGeneratedDrain().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })]),
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
      expectGeneratedDrain().flush(documentPage([]));
      await harness.fixture.whenStable();

      const empty = element().querySelector('app-empty-state');
      expect(empty?.textContent).toContain('No generated documents yet');
      expect(empty?.textContent).not.toContain('Nothing uploaded yet');

      empty?.querySelector<HTMLButtonElement>('button')?.click();
      await harness.fixture.whenStable();

      // The button removed itself with its branch, so focus has to land somewhere real.
      expect(document.activeElement).toBe(sourceSwitch());
      expect(url()).toBe('/documents');
      expectDrain().flush(documentPage(twoConversations()));
      await harness.fixture.whenStable();
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });

    it('drops a name term with the filter, so the way out shows every document', async () => {
      await harness.navigateByUrl('/documents?source=generated&name=summary', Documents);
      expectGeneratedDrain().flush(documentPage([]));
      await harness.fixture.whenStable();

      element().querySelector<HTMLButtonElement>('app-empty-state button')?.click();
      await harness.fixture.whenStable();

      // Leaving `?name=` behind would land the reader on the no-matches card instead.
      expect(url()).toBe('/documents');
      expectDrain().flush(documentPage(twoConversations()));
      await harness.fixture.whenStable();
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });

    it('does not re-drain when only the term changes', async () => {
      await harness.navigateByUrl('/documents?source=generated', Documents);
      expectGeneratedDrain().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })]),
      );
      await harness.fixture.whenStable();

      await type('summary');

      // Every `?name=` navigation re-sets the `source` input; input equality is what
      // stops that re-running the drain, and `afterEach`'s verify() is what proves it.
      expect(url()).toContain('source=generated');
    });

    it('lets the term and the origin survive each other', async () => {
      await open('/documents?name=summary', twoConversations());

      sourceSwitch().click();
      await harness.fixture.whenStable();

      expect(url()).toContain('name=summary');
      expect(url()).toContain('source=generated');
      expectGeneratedDrain().flush(
        documentPage([userDocumentFixture({ name: 'summary.docx', type: 'Generated' })]),
      );
      await harness.fixture.whenStable();

      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
    });
  });

  describe('the grouped view (US-1003)', () => {
    it('draws a linking header with a count, and rows without the conversation column', async () => {
      const alpha = userDocumentFixture({ name: 'alpha-plan.pdf', conversationName: 'Alpha' });
      const beta = userDocumentFixture({ name: 'beta-notes.txt', conversationName: 'Beta' });
      const alphaOlder = userDocumentFixture({
        name: 'alpha-summary.md',
        conversationId: alpha.conversationId,
        conversationName: 'Alpha',
      });
      await open('/documents', [alpha, beta, alphaOlder]);

      const headers = [...element().querySelectorAll('.documents__group-header')];
      expect(headers).toHaveLength(2);

      const name = headers[0]?.querySelector<HTMLAnchorElement>('.documents__group-name');
      expect(name?.textContent?.trim()).toBe('Alpha');
      expect(name?.getAttribute('href')).toBe(`/chat/${alpha.conversationId}`);
      expect(headers[0]?.querySelector('.documents__group-count')?.textContent?.trim()).toBe(
        '2 files',
      );
      expect(headers[1]?.querySelector('.documents__group-count')?.textContent?.trim()).toBe(
        '1 file',
      );

      // The header names the conversation, so the rows do not repeat it.
      expect(element().querySelector('.document-row__conversation')).toBeNull();
      // The badge stays on every row in both shapes.
      expect(element().querySelectorAll('app-source-badge')).toHaveLength(3);
    });

    it('collapses a group from its chevron, removing its rows from the DOM', async () => {
      await open('/documents', twoConversations());
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);

      const chevron = element().querySelector<HTMLButtonElement>('.documents__group-chevron');
      expect(chevron?.getAttribute('aria-expanded')).toBe('true');
      expect(chevron?.getAttribute('aria-controls')).not.toBeNull();

      chevron!.click();
      await harness.fixture.whenStable();

      expect(chevron?.getAttribute('aria-expanded')).toBe('false');
      expect(element().querySelectorAll('app-document-row')).toHaveLength(1);
      expect(element().querySelectorAll('.documents__group-header')).toHaveLength(2);

      chevron!.click();
      await harness.fixture.whenStable();

      expect(chevron?.getAttribute('aria-expanded')).toBe('true');
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
    });
  });

  describe('the empty states', () => {
    it('renders the never-uploaded state, with its exact copy and no action', async () => {
      await open('/documents', []);

      const empty = element().querySelector('app-empty-state');
      expect(empty).not.toBeNull();
      expect(empty?.querySelector('app-ridgeline')).not.toBeNull();
      expect(empty?.querySelector('.empty-state__heading')?.textContent?.trim()).toBe(
        'Nothing uploaded yet',
      );
      expect(empty?.querySelector('.empty-state__message')?.textContent?.trim()).toBe(
        'Attach files in any conversation and they’ll appear here.',
      );
      expect(empty?.querySelector('button')).toBeNull();
      expect(empty?.querySelector('a')).toBeNull();
    });

    it('tells the truth on a zero-document deep link that carries a term', async () => {
      await open('/documents?name=ghost', []);

      // "Nothing uploaded yet", not "No documents match": clearing the filter
      // would reveal nothing, so offering it would be a lie.
      expect(element().querySelector('.empty-state__heading')?.textContent?.trim()).toBe(
        'Nothing uploaded yet',
      );
    });

    it('offers the no-matches state, naming the term, and clears back out of it', async () => {
      await open('/documents', twoConversations());

      await type('zzz');

      const empty = element().querySelector('app-empty-state');
      expect(empty?.querySelector('.empty-state__heading')?.textContent?.trim()).toBe(
        'No documents match “zzz”',
      );
      expect(empty?.querySelector('.empty-state__message')?.textContent?.trim()).toBe(
        'Filters cover file names only.',
      );

      empty?.querySelector<HTMLButtonElement>('button')?.click();
      await harness.fixture.whenStable();

      // Clearing is a navigation and a client-side re-widen — never a request.
      backend.expectNone(() => true);
      expect(url()).toBe('/documents');
      expect(element().querySelectorAll('app-document-row')).toHaveLength(2);
      // The button removed itself with the empty state, so focus lands in the field.
      expect(document.activeElement?.classList.contains('search__field')).toBe(true);
    });

    it('qualifies the no-matches state over a truncated drain rather than lying', async () => {
      await harness.navigateByUrl('/documents', Documents);

      for (let page = 0; page * DOCUMENT_PAGE_SIZE < DOCUMENT_LOAD_CEILING; page++) {
        expectDrain().flush(
          documentPage(
            Array.from({ length: DOCUMENT_PAGE_SIZE }, () => userDocumentFixture()),
            { totalCount: 600, skip: page * DOCUMENT_PAGE_SIZE },
          ),
        );
        await harness.fixture.whenStable();
      }

      await type('zzz');

      // A match may sit in the 100 unloaded rows, so the scope line yields to the
      // partial-set qualifier instead of claiming the whole library was searched.
      const empty = element().querySelector('app-empty-state');
      expect(empty?.querySelector('.empty-state__heading')?.textContent?.trim()).toBe(
        'No documents match “zzz”',
      );
      expect(empty?.querySelector('.empty-state__message')?.textContent?.trim()).toBe(
        'Only the 500 most recent documents have loaded.',
      );
    });
  });

  describe('the count summary', () => {
    it('shows the count aria-hidden and announces it once through the status region', async () => {
      await open('/documents', twoConversations());

      const visible = element().querySelector('.documents__count');
      expect(visible?.textContent?.trim()).toBe('2 documents');
      expect(visible?.getAttribute('aria-hidden')).toBe('true');

      const status = element().querySelector('[role="status"]');
      expect(status?.textContent?.trim()).toBe('2 documents');

      await type('alpha');

      expect(element().querySelector('.documents__count')?.textContent?.trim()).toBe(
        'Showing 1 of 2 documents',
      );
    });

    it('withholds the count while the first drain runs', async () => {
      await harness.navigateByUrl('/documents', Documents);

      expect(element().querySelector('.documents__count')).toBeNull();
      expect(element().querySelector('[role="status"]')?.textContent?.trim()).toBe('');

      expectDrain().flush(documentPage([userDocumentFixture()]));
      await harness.fixture.whenStable();

      expect(element().querySelector('.documents__count')?.textContent?.trim()).toBe('1 document');
    });
  });
});
