import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ModelDto } from '@domain/api/model';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { Conversations } from './conversations';
import { shouldReplaceHistory } from './conversations-route';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

/**
 * `SearchInput`'s own 300 ms window, with room for the effect that follows it and
 * for a loaded CI runner — these are real timers, because faking the clock also
 * stalls the scheduler `whenStable()` awaits.
 */
const DEBOUNCE_MS = 450;

describe('shouldReplaceHistory', () => {
  it('pushes when a filter goes on, so back returns to the unfiltered list', () => {
    expect(shouldReplaceHistory('', 'quarterly')).toBe(false);
  });

  it('pushes when a filter comes off, so clearing is undoable too', () => {
    expect(shouldReplaceHistory('quarterly', '')).toBe(false);
  });

  it('replaces while a term is being refined', () => {
    // Otherwise every debounce leaves an entry and back walks the reader through
    // their own typing one keystroke at a time.
    expect(shouldReplaceHistory('quar', 'quarterly')).toBe(true);
  });
});

describe('Conversations (US-701)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let models: ReturnType<typeof signal<readonly ModelDto[]>>;

  beforeEach(async () => {
    models = signal<readonly ModelDto[]>([]);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        // Stubbed rather than loaded: `SessionBootstrap` fills the catalogue in
        // the application, and this screen only ever reads it.
        { provide: ModelCatalogStore, useValue: { models } },
        provideRouter(
          [{ path: 'conversations', component: Conversations }],
          withComponentInputBinding(),
        ),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    // The harness navigates directly and never starts the router's popstate
    // listener, so without this a back press moves `Location` and leaves the
    // router — and the screen — on the URL it already had.
    TestBed.inject(Router).setUpLocationChangeListener();
  });

  afterEach(() => {
    backend.verify();
  });

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function expectSearch(): TestRequest {
    return backend.expectOne((request) => request.url === SEARCH_URL);
  }

  /** Opens the screen and answers its first query with `items`. */
  async function open(url = '/conversations', items = [conversationFixture()]): Promise<void> {
    await harness.navigateByUrl(url, Conversations);
    expectSearch().flush(conversationPage(items));
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

  describe('searching by name', () => {
    it('waits for the pause, then sends name= and never filter=', async () => {
      await open();

      const field = element().querySelector<HTMLInputElement>('.search__field');
      field!.value = 'quarterly';
      field!.dispatchEvent(new Event('input'));
      await harness.fixture.whenStable();
      // `SearchInput` debounces, which is why the store carries no second one.
      backend.expectNone((request) => request.url === SEARCH_URL);

      await new Promise((resolve) => setTimeout(resolve, DEBOUNCE_MS));
      await harness.fixture.whenStable();

      const request = expectSearch();
      expect(request.request.params.get('name')).toBe('quarterly');
      expect(request.request.params.has('filter')).toBe(false);
      request.flush(conversationPage([]));
      await harness.fixture.whenStable();
    });

    it('omits the parameter entirely when the term is cleared', async () => {
      await open('/conversations?name=quarterly', []);

      await type('');

      const request = expectSearch();
      // Not `name=`: an empty value would still take the server down its LIKE branch.
      expect(request.request.params.has('name')).toBe(false);
      request.flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();
    });

    it('leaves the sidebar’s own list alone', async () => {
      await open();
      await type('quarterly');
      expectSearch().flush(conversationPage([]));
      await harness.fixture.whenStable();

      // The whole reason this screen has a store of its own: frame 4a draws both
      // fields at once, and a library search must not retype the sidebar's.
      expect(TestBed.inject(ConversationListStore).name()).toBe('');
    });
  });

  describe('the URL', () => {
    it('reflects the term, so a filtered list is shareable', async () => {
      await open();
      await type('quarterly');
      expectSearch().flush(conversationPage([]));
      await harness.fixture.whenStable();

      expect(TestBed.inject(Router).url).toBe('/conversations?name=quarterly');
    });

    it('issues exactly one request for a link opened cold', async () => {
      // Handing `bindQuery` the *signal* is what makes this one request: reading
      // its value in the constructor would fire unfiltered first, filtered second.
      await harness.navigateByUrl('/conversations?name=quarterly', Conversations);

      const request = expectSearch();
      expect(request.request.params.get('name')).toBe('quarterly');
      request.flush(conversationPage([]));
      await harness.fixture.whenStable();

      expect(element().querySelector<HTMLInputElement>('.search__field')?.value).toBe('quarterly');
    });

    it('restores the prior list when the back button is pressed', async () => {
      await open();
      await type('quarterly');
      expectSearch().flush(conversationPage([]));
      await harness.fixture.whenStable();

      await goBack();

      expect(TestBed.inject(Router).url).toBe('/conversations');
      const request = expectSearch();
      expect(request.request.params.has('name')).toBe(false);
      request.flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();
    });

    it('does not stack an entry per keystroke', async () => {
      await open();
      await type('quar');
      expectSearch().flush(conversationPage([]));
      await harness.fixture.whenStable();

      await type('quarterly');
      expectSearch().flush(conversationPage([]));
      await harness.fixture.whenStable();

      // One press, not two: refining replaced rather than pushed.
      await goBack();

      expect(TestBed.inject(Router).url).toBe('/conversations');
      expectSearch().flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();
    });
  });

  describe('the empty states', () => {
    it('renders frame 4b, naming the term', async () => {
      await open('/conversations?name=quarterly', []);

      const empty = element().querySelector('app-empty-state');
      expect(empty?.querySelector('app-ridgeline')).not.toBeNull();
      expect(empty?.querySelector('.empty-state__heading')?.textContent?.trim()).toBe(
        'No conversations match “quarterly”',
      );
      expect(empty?.querySelector('.empty-state__message')?.textContent?.trim()).toBe(
        'Search covers conversation names only.',
      );
      expect(empty?.querySelector('button')?.textContent?.trim()).toBe('Clear search');
    });

    it('clears the search from that state, and puts focus back in the field', async () => {
      await open('/conversations?name=quarterly', []);

      element().querySelector<HTMLButtonElement>('app-empty-state button')?.click();
      await harness.fixture.whenStable();

      const request = expectSearch();
      expect(request.request.params.has('name')).toBe(false);
      request.flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();

      expect(TestBed.inject(Router).url).toBe('/conversations');
      // The button removes itself when the rows come back, so focus would
      // otherwise fall to <body> inside a list the reader has to find again.
      expect(document.activeElement?.classList.contains('search__field')).toBe(true);
    });

    it('offers a different state, and a different action, when there are none at all', async () => {
      await open('/conversations', []);

      const empty = element().querySelector('app-empty-state');
      expect(empty?.querySelector('.empty-state__heading')?.textContent?.trim()).toBe(
        'No conversations yet',
      );
      expect(empty?.querySelector('button')).toBeNull();
      expect(empty?.querySelector('a')?.textContent?.trim()).toBe('New conversation');
    });
  });

  describe('the rest of the screen', () => {
    it('links each row to its conversation', async () => {
      const conversation = conversationFixture({ name: 'Quarterly review' });
      await open('/conversations', [conversation]);

      const link = element().querySelector('.conversations__name');
      expect(link?.textContent?.trim()).toBe('Quarterly review');
      expect(link?.getAttribute('href')).toBe(`/chat/${conversation.id}`);
    });

    it('names each row’s model, and says so honestly when it has none', async () => {
      const model = modelFixture({ name: 'GPT-5' });
      models.set([model]);
      await open('/conversations', [
        conversationFixture({ modelId: model.id }),
        conversationFixture({ modelId: null }),
      ]);

      // The column renders through a cell template keyed on `columns[1].key`; if
      // the two ever diverge `DataTable` falls back to `column.text`, which is
      // undefined here, and the column would silently render blank.
      const cells = [...element().querySelectorAll('.conversations__model')].map((cell) =>
        cell.textContent?.trim(),
      );
      expect(cells).toEqual(['GPT-5', '—']);
    });

    it('shows a skeleton for the first load, and never for a refinement', async () => {
      await harness.navigateByUrl('/conversations', Conversations);
      expect(element().querySelector('.table-skeleton')).not.toBeNull();

      expectSearch().flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();
      expect(element().querySelector('.table-skeleton')).toBeNull();

      await type('quarterly');
      // Narrowing keeps the rows on screen and marks the field busy instead of
      // flashing an empty frame between two result sets.
      expect(element().querySelector('.table-skeleton')).toBeNull();
      expect(element().querySelectorAll('.conversations__name')).toHaveLength(1);

      expectSearch().flush(conversationPage([]));
      await harness.fixture.whenStable();
    });

    it('offers a retry on a failure, instead of the table', async () => {
      await harness.navigateByUrl('/conversations', Conversations);
      expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
        status: 500,
        statusText: 'Internal Server Error',
      });
      await harness.fixture.whenStable();

      expect(element().querySelector('app-error-panel')).not.toBeNull();
      expect(element().querySelector('app-data-table')).toBeNull();

      element().querySelector<HTMLButtonElement>('app-error-panel button')?.click();
      await harness.fixture.whenStable();

      expectSearch().flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();
      expect(element().querySelector('app-error-panel')).toBeNull();
    });

    it('offers no sort control, because the server cannot order this list', async () => {
      await open();

      // US-705's subject, and true for free until US-706 gives the endpoint a
      // sort parameter to honour.
      expect(element().querySelector('select')).toBeNull();
      expect(element().querySelector('th button')).toBeNull();
    });
  });
});
