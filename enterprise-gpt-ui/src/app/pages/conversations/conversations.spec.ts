import { Location } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ModelDto } from '@shared/models/api/chat-model.model';
import { shouldReplaceHistory } from '@shared/utils/navigation/conversations-route';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { ToastStore } from '@core/notifications/toast-store';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { LIBRARY_PAGE_SIZE } from '@state/conversations/conversation-library-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { NARROW_VIEWPORT, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { projectFixture, projectPage } from '@testing/projects';
import { Conversations } from './conversations';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

/**
 * `SearchInput`'s 300 ms window plus slack for a loaded CI runner. Real timers: faking the clock
 * also stalls the scheduler `whenStable()` awaits.
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
    expect(shouldReplaceHistory('quar', 'quarterly')).toBe(true);
  });
});

describe('Conversations', () => {
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
        // Stubbed: `SessionBootstrap` fills the catalogue in the app; this screen only reads it.
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
    // The harness never starts the router's popstate listener, so without this back would move
    // `Location` but not the router.
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

  /** Presses back and waits a macrotask, since the router handles the popstate asynchronously. */
  async function goBack(): Promise<void> {
    TestBed.inject(Location).back();
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
  }

  /** Types into the field and waits out the debounce on real timers. */
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

      // The library has its own store so its search never narrows the sidebar's.
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
      // One request because `bindQuery` gets a computation, not the constructor-time value.
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

      // Rendered through a cell template keyed on the column's key; if they diverge, `DataTable`
      // falls back to the undefined `column.text` and the column renders blank.
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
  });

  describe('choosing the order', () => {
    function sortSelect(): HTMLSelectElement {
      const control = element().querySelector<HTMLSelectElement>('.conversations__sort select');

      if (control === null) {
        throw new Error('The sort select is not rendered.');
      }

      return control;
    }

    async function choose(value: string): Promise<void> {
      const control = sortSelect();
      control.value = value;
      control.dispatchEvent(new Event('change'));
      await harness.fixture.whenStable();
    }

    it('offers a sort control rather than a caption stating the order', async () => {
      await open();

      // No caption explaining a fixed order, and no flyout for it.
      expect(element().querySelector('.conversations__order')).toBeNull();
      expect(element().querySelector('.conversations__order-info')).toBeNull();

      const options = [...sortSelect().querySelectorAll('option')];
      expect(options.map((option) => option.value)).toEqual(['newest', 'oldest', 'name']);
      expect(sortSelect().value).toBe('newest');
      expect(
        element().querySelector('.conversations__sort .visually-hidden')?.textContent?.trim(),
      ).toBe('Sort conversations');
    });

    it('sends the API pair the chosen order maps to, and records it in the URL', async () => {
      await open();

      await choose('oldest');

      expect(TestBed.inject(Router).url).toBe('/conversations?sort=oldest');
      const reordered = expectSearch();
      expect(reordered.request.params.get('sort')).toBe('created');
      expect(reordered.request.params.get('dir')).toBe('asc');
      reordered.flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();

      // `[selected]` is written only when its expression changes, so pin the control's value.
      expect(sortSelect().value).toBe('oldest');
    });

    it('replaces between two orders, so arrowing through the select does not stack history', async () => {
      await open('/conversations?sort=oldest');

      await choose('name');
      expectSearch().flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();
      expect(TestBed.inject(Router).url).toBe('/conversations?sort=name');

      await goBack();

      expect(TestBed.inject(Router).url).not.toBe('/conversations?sort=oldest');
    });

    it('removes the key rather than spelling out the default it returns to', async () => {
      await open('/conversations?sort=name');

      await choose('newest');

      expect(TestBed.inject(Router).url).toBe('/conversations');
      expectSearch().flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();
    });

    it('falls back to the default when the URL names an order that does not exist', async () => {
      await harness.navigateByUrl('/conversations?sort=zzz', Conversations);

      const request = expectSearch();
      expect(request.request.params.get('sort')).toBe('created');
      expect(request.request.params.get('dir')).toBe('desc');
      request.flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();

      expect(sortSelect().value).toBe('newest');
    });

    it('carries the order onto the next page, so Load more continues the same run', async () => {
      await harness.navigateByUrl('/conversations?sort=name', Conversations);
      expectSearch().flush(
        conversationPage([conversationFixture(), conversationFixture()], {
          totalCount: 4,
          pageSize: LIBRARY_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      element().querySelector<HTMLButtonElement>('.conversations__more button')!.click();
      await harness.fixture.whenStable();

      const next = expectSearch();
      expect(next.request.params.get('sort')).toBe('name');
      expect(next.request.params.get('dir')).toBe('asc');
      next.flush(
        conversationPage([conversationFixture()], {
          totalCount: 4,
          pageSize: LIBRARY_PAGE_SIZE,
          skip: 2,
        }),
      );
      await harness.fixture.whenStable();
    });

    it('replaces the rows when the order changes rather than appending to them', async () => {
      await open('/conversations', [
        conversationFixture({ name: 'Zulu' }),
        conversationFixture({ name: 'Alpha' }),
      ]);

      await choose('name');
      expectSearch().flush(conversationPage([conversationFixture({ name: 'Alpha' })]));
      await harness.fixture.whenStable();

      expect(
        [...element().querySelectorAll('.conversations__name')].map((row) =>
          row.textContent?.trim(),
        ),
      ).toEqual(['Alpha']);
    });

    it('applies no client-side reordering to a partially loaded list', async () => {
      // Deliberately out of alphabetical order, so any sort at all would be visible.
      const first = [conversationFixture({ name: 'Zulu' }), conversationFixture({ name: 'Alpha' })];
      await harness.navigateByUrl('/conversations', Conversations);
      expectSearch().flush(conversationPage(first, { totalCount: 4, pageSize: LIBRARY_PAGE_SIZE }));
      await harness.fixture.whenStable();

      element().querySelector<HTMLButtonElement>('.conversations__more button')!.click();
      await harness.fixture.whenStable();
      expectSearch().flush(
        conversationPage(
          [conversationFixture({ name: 'Mike' }), conversationFixture({ name: 'Bravo' })],
          { totalCount: 4, pageSize: LIBRARY_PAGE_SIZE, skip: 2 },
        ),
      );
      await harness.fixture.whenStable();

      expect(
        [...element().querySelectorAll('.conversations__name')].map((row) =>
          row.textContent?.trim(),
        ),
      ).toEqual(['Zulu', 'Alpha', 'Mike', 'Bravo']);
    });
  });

  describe('filtering to favourites', () => {
    function filterButton(): HTMLButtonElement {
      const button = element().querySelector<HTMLButtonElement>('.conversations__filter');
      expect(button).not.toBeNull();
      return button!;
    }

    function url(): string {
      return TestBed.inject(Router).url;
    }

    it('puts the filter in the URL and sends isFavorite=true', async () => {
      await open();
      expect(filterButton().getAttribute('aria-pressed')).toBe('false');

      filterButton().click();
      await harness.fixture.whenStable();

      expect(url()).toBe('/conversations?favorites=1');
      expect(expectSearch().request.params.get('isFavorite')).toBe('true');
      backend.expectNone(() => true);
      expect(filterButton().getAttribute('aria-pressed')).toBe('true');
    });

    it('restores the unfiltered list on back, because turning it on pushes', async () => {
      await open();
      filterButton().click();
      await harness.fixture.whenStable();
      expectSearch().flush(conversationPage([]));
      await harness.fixture.whenStable();

      await goBack();

      expect(url()).toBe('/conversations');
      const request = expectSearch();
      expect(request.request.params.has('isFavorite')).toBe(false);
      request.flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();
    });

    it('lets a search and the filter survive each other', async () => {
      await open('/conversations?name=quarterly');

      filterButton().click();
      await harness.fixture.whenStable();

      expect(url()).toContain('name=quarterly');
      expect(url()).toContain('favorites=1');
      const request = expectSearch();
      expect(request.request.params.get('name')).toBe('quarterly');
      expect(request.request.params.get('isFavorite')).toBe('true');
      request.flush(conversationPage([]));
      await harness.fixture.whenStable();
    });

    it('ignores a favourites value it never writes', async () => {
      await open('/conversations?favorites=yes');

      expect(filterButton().getAttribute('aria-pressed')).toBe('false');
    });

    it('stars a row through the shared action, and marks it busy meanwhile', async () => {
      const conversation = conversationFixture({ name: 'Helios', isFavorite: false });
      await open('/conversations', [conversation]);

      const star = element().querySelector<HTMLButtonElement>('.conversations__star');
      expect(star?.getAttribute('aria-label')).toBe('Favourite Helios');

      star!.click();
      await harness.fixture.whenStable();

      const request = backend.expectOne(
        `${TEST_API_BASE_URL}/api/conversations/${conversation.id}/favorite`,
      );
      expect(request.request.body).toEqual({ isFavorite: true });
      // aria-disabled, not `disabled`, which would blur the button mid-press.
      expect(element().querySelector('.conversations__star')?.getAttribute('aria-disabled')).toBe(
        'true',
      );
      expect(element().querySelector<HTMLButtonElement>('.conversations__star')?.disabled).toBe(
        false,
      );

      request.flush(null, { status: 204, statusText: 'No Content' });
      await harness.fixture.whenStable();

      // The 204 has no body; the row updates from the actions store's confirmed event.
      expect(
        element().querySelector('.conversations__star')?.getAttribute('aria-disabled'),
      ).toBeNull();
      expect(element().querySelector('.conversations__star')?.getAttribute('aria-label')).toBe(
        'Unfavourite Helios',
      );
    });

    it('drops a row from the filtered list when its star is cleared', async () => {
      const conversation = conversationFixture({ name: 'Helios', isFavorite: true });
      await open('/conversations?favorites=1', [conversation]);

      element().querySelector<HTMLButtonElement>('.conversations__star')!.click();
      await harness.fixture.whenStable();

      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}/favorite`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await harness.fixture.whenStable();

      expect(element().querySelectorAll('.conversations__name')).toHaveLength(0);
      expect(element().querySelector('app-empty-state')?.textContent).toContain(
        'No favourite conversations',
      );
      // The pressed star went with the row.
      expect(document.activeElement).toBe(element().querySelector('.search__field'));
    });

    it('offers its own empty state and way out when nothing is starred', async () => {
      await open('/conversations?favorites=1', []);

      const empty = element().querySelector('app-empty-state');
      expect(empty?.textContent).toContain('No favourite conversations');
      expect(empty?.textContent).toContain('Star a conversation and it will appear here.');

      element().querySelector<HTMLButtonElement>('app-empty-state button')?.click();
      await harness.fixture.whenStable();

      expect(url()).toBe('/conversations');
      expectSearch().flush(conversationPage([conversationFixture()]));
      await harness.fixture.whenStable();
    });
  });

  describe('deleting several at once', () => {
    function rowCheckboxes(): HTMLInputElement[] {
      return [...element().querySelectorAll<HTMLInputElement>('tbody input[type="checkbox"]')];
    }

    function selectAll(): HTMLInputElement {
      const box = element().querySelector<HTMLInputElement>('.conversations__select-all input');
      expect(box).not.toBeNull();
      return box!;
    }

    function bulkBar(): HTMLElement | null {
      return element().querySelector('.bulk-bar');
    }

    async function openWithRows(count = 3): Promise<void> {
      await open(
        '/conversations',
        Array.from({ length: count }, (_, index) => conversationFixture({ name: `Row ${index}` })),
      );
    }

    it('raises the pill bar with the count and the reach of select-all', async () => {
      await openWithRows();
      expect(bulkBar()).toBeNull();

      rowCheckboxes()[0]!.click();
      await harness.fixture.whenStable();

      expect(bulkBar()?.textContent).toContain('1 selected');
      expect(bulkBar()?.textContent).toContain('Select-all covers the 3 loaded rows only');
      expect(bulkBar()?.textContent).toContain('Delete selected');
    });

    it('puts select-all in the toolbar, not the table header', async () => {
      await openWithRows();

      expect(element().querySelector('thead input[type="checkbox"]')).toBeNull();

      selectAll().click();
      await harness.fixture.whenStable();

      expect(bulkBar()?.textContent).toContain('3 selected');
      expect(rowCheckboxes().every((box) => box.checked)).toBe(true);
      expect(selectAll().indeterminate).toBe(false);
    });

    it('reports a partial selection as indeterminate', async () => {
      await openWithRows();

      rowCheckboxes()[0]!.click();
      await harness.fixture.whenStable();

      expect(selectAll().checked).toBe(false);
      expect(selectAll().indeterminate).toBe(true);
    });

    it('confirms first, then deletes the batch in one request', async () => {
      await openWithRows();
      selectAll().click();
      await harness.fixture.whenStable();

      element().querySelector<HTMLButtonElement>('.bulk-bar .btn-danger')!.click();
      await harness.fixture.whenStable();

      const dialog = element().querySelector('app-modal');
      expect(dialog?.textContent).toContain('Delete 3 conversations?');
      backend.expectNone(() => true);

      element().querySelectorAll<HTMLButtonElement>('app-modal [modalFooter]')[1]!.click();
      await harness.fixture.whenStable();

      const request = backend.expectOne(`${TEST_API_BASE_URL}/api/conversations/bulk`);
      expect((request.request.body as { conversationIds: string[] }).conversationIds).toHaveLength(
        3,
      );

      // Optimistic: the rows and the bar are gone before the server answers.
      expect(element().querySelectorAll('.conversations__name')).toHaveLength(0);
      expect(bulkBar()).toBeNull();

      request.flush(null, { status: 204, statusText: 'No Content' });
      await harness.fixture.whenStable();
    });

    it('cancelling the confirmation leaves the rows and the selection alone', async () => {
      await openWithRows();
      rowCheckboxes()[0]!.click();
      await harness.fixture.whenStable();

      element().querySelector<HTMLButtonElement>('.bulk-bar .btn-danger')!.click();
      await harness.fixture.whenStable();
      element().querySelectorAll<HTMLButtonElement>('app-modal [modalFooter]')[0]!.click();
      await harness.fixture.whenStable();

      backend.expectNone(() => true);
      expect(element().querySelectorAll('.conversations__name')).toHaveLength(3);
      expect(bulkBar()?.textContent).toContain('1 selected');
    });

    it('restores every row behind one toast when the batch fails', async () => {
      await openWithRows();
      selectAll().click();
      await harness.fixture.whenStable();
      element().querySelector<HTMLButtonElement>('.bulk-bar .btn-danger')!.click();
      await harness.fixture.whenStable();
      element().querySelectorAll<HTMLButtonElement>('app-modal [modalFooter]')[1]!.click();
      await harness.fixture.whenStable();

      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/bulk`)
        .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
          status: 500,
          statusText: 'Internal Server Error',
        });
      await harness.fixture.whenStable();

      expect(
        [...element().querySelectorAll('.conversations__name')].map((row) =>
          row.textContent?.trim(),
        ),
      ).toEqual(['Row 0', 'Row 1', 'Row 2']);
      // Re-selected, so Retry is one press.
      expect(bulkBar()?.textContent).toContain('3 selected');
      expect(TestBed.inject(ToastStore).toasts()).toHaveLength(1);
    });

    it('takes the sidebar’s own list down with it', async () => {
      const rows = [conversationFixture({ name: 'Row 0' }), conversationFixture({ name: 'Row 1' })];
      const sidebar = TestBed.inject(ConversationListStore);
      sidebar.ensureLoaded();
      await harness.fixture.whenStable();
      backend
        .expectOne((request) => request.url === SEARCH_URL)
        .flush(conversationPage(rows, { totalCount: 9 }));
      await harness.fixture.whenStable();
      expect(sidebar.entities()).toHaveLength(2);

      await open('/conversations', rows);
      selectAll().click();
      await harness.fixture.whenStable();
      element().querySelector<HTMLButtonElement>('.bulk-bar .btn-danger')!.click();
      await harness.fixture.whenStable();
      element().querySelectorAll<HTMLButtonElement>('app-modal [modalFooter]')[1]!.click();
      await harness.fixture.whenStable();

      // Rows left in the sidebar would 404 on click and put its next page one window late.
      expect(sidebar.entities()).toHaveLength(0);
      expect(sidebar.totalCount()).toBe(7);

      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/bulk`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await harness.fixture.whenStable();
    });

    it('puts the sidebar’s rows back in the order they were in', async () => {
      const rows = [
        conversationFixture({ name: 'Row 0' }),
        conversationFixture({ name: 'Row 1' }),
        conversationFixture({ name: 'Row 2' }),
      ];
      const sidebar = TestBed.inject(ConversationListStore);
      sidebar.ensureLoaded();
      await harness.fixture.whenStable();
      backend
        .expectOne((request) => request.url === SEARCH_URL)
        .flush(conversationPage(rows, { totalCount: 3 }));
      await harness.fixture.whenStable();

      await open('/conversations', rows);
      // The first two, top-down: the order a naive restore gets wrong.
      rowCheckboxes()[0]!.click();
      rowCheckboxes()[1]!.click();
      await harness.fixture.whenStable();
      element().querySelector<HTMLButtonElement>('.bulk-bar .btn-danger')!.click();
      await harness.fixture.whenStable();
      element().querySelectorAll<HTMLButtonElement>('app-modal [modalFooter]')[1]!.click();
      await harness.fixture.whenStable();

      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/bulk`)
        .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
          status: 500,
          statusText: 'Internal Server Error',
        });
      await harness.fixture.whenStable();

      // Each removal captured its index against a list one row shorter than the last,
      // so only a LIFO restore is their inverse.
      expect(sidebar.entities().map((row) => row.name)).toEqual(['Row 0', 'Row 1', 'Row 2']);
      expect(sidebar.totalCount()).toBe(3);
    });

    it('clears the selection from the bar', async () => {
      await openWithRows();
      selectAll().click();
      await harness.fixture.whenStable();

      element().querySelector<HTMLButtonElement>('.bulk-bar__clear')!.click();
      await harness.fixture.whenStable();

      expect(bulkBar()).toBeNull();
      expect(rowCheckboxes().some((box) => box.checked)).toBe(false);
    });
  });

  describe('paging', () => {
    function loadMoreButton(): HTMLButtonElement | null {
      return element().querySelector<HTMLButtonElement>('.conversations__more button');
    }

    function rowNames(): (string | undefined)[] {
      return [...element().querySelectorAll('.conversations__name')].map((row) =>
        row.textContent?.trim(),
      );
    }

    /** Opens with a full first page that leaves more behind. */
    async function openPaged(totalCount = 60): Promise<void> {
      const items = Array.from({ length: LIBRARY_PAGE_SIZE }, (_, index) =>
        conversationFixture({ name: `Row ${index}` }),
      );
      await harness.navigateByUrl('/conversations', Conversations);
      expectSearch().flush(conversationPage(items, { totalCount, pageSize: LIBRARY_PAGE_SIZE }));
      await harness.fixture.whenStable();
    }

    it('counts the loaded rows against the envelope total', async () => {
      await openPaged(312);

      expect(element().querySelector('.conversations__count')?.textContent?.trim()).toBe(
        'Showing 25 of 312 conversations',
      );
    });

    it('hands the same count to the table’s live region rather than a second one', async () => {
      await openPaged(312);

      expect(element().querySelector('.conversations__count')?.getAttribute('aria-hidden')).toBe(
        'true',
      );
      const summary = element().querySelector('[aria-live="polite"]');
      expect(summary?.textContent?.trim()).toBe('Showing 25 of 312 conversations');
    });

    it('appends the next page in server order and updates the counter', async () => {
      await openPaged();

      loadMoreButton()!.click();
      await harness.fixture.whenStable();

      const request = expectSearch();
      expect(request.request.params.get('skip')).toBe(String(LIBRARY_PAGE_SIZE));
      request.flush(
        conversationPage([conversationFixture({ name: 'Row 25' })], {
          totalCount: 60,
          pageSize: LIBRARY_PAGE_SIZE,
          skip: LIBRARY_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      const names = rowNames();
      expect(names).toHaveLength(LIBRARY_PAGE_SIZE + 1);
      expect(names.at(0)).toBe('Row 0');
      expect(names.at(-1)).toBe('Row 25');
      expect(element().querySelector('.conversations__count')?.textContent?.trim()).toBe(
        'Showing 26 of 60 conversations',
      );
    });

    it('removes the control rather than disabling it once everything is loaded', async () => {
      await openPaged(LIBRARY_PAGE_SIZE);

      expect(loadMoreButton()).toBeNull();
    });

    it('marks the control busy while a page is on the wire', async () => {
      await openPaged();

      loadMoreButton()!.click();
      await harness.fixture.whenStable();
      expect(loadMoreButton()?.getAttribute('aria-disabled')).toBe('true');

      expectSearch().flush(
        conversationPage([], { totalCount: 60, pageSize: LIBRARY_PAGE_SIZE, skip: 25 }),
      );
      await harness.fixture.whenStable();
    });

    it('marks it busy during a narrowing search too, when pressing it would do nothing', async () => {
      await openPaged();

      await type('quarterly');
      // `loadMore()` refuses while `isPending()`, so the control must not look operable.
      expect(loadMoreButton()?.getAttribute('aria-disabled')).toBe('true');

      expectSearch().flush(conversationPage([], { pageSize: LIBRARY_PAGE_SIZE }));
      await harness.fixture.whenStable();
    });

    it('moves focus to the last row when the control removes itself', async () => {
      await openPaged(LIBRARY_PAGE_SIZE + 1);

      const button = loadMoreButton()!;
      button.focus();
      button.click();
      await harness.fixture.whenStable();

      expectSearch().flush(
        conversationPage([conversationFixture({ name: 'Last' })], {
          totalCount: LIBRARY_PAGE_SIZE + 1,
          pageSize: LIBRARY_PAGE_SIZE,
          skip: LIBRARY_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(loadMoreButton()).toBeNull();
      expect(document.activeElement?.textContent?.trim()).toBe('Last');
    });

    it('leaves focus on the control while there is still more to load', async () => {
      await openPaged(100);

      const button = loadMoreButton()!;
      button.focus();
      button.click();
      await harness.fixture.whenStable();

      expectSearch().flush(
        conversationPage([conversationFixture()], {
          totalCount: 100,
          pageSize: LIBRARY_PAGE_SIZE,
          skip: LIBRARY_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(document.activeElement).toBe(loadMoreButton());
    });

    it('leaves focus where the reader put it while the page was on the wire', async () => {
      await openPaged(LIBRARY_PAGE_SIZE + 1);

      loadMoreButton()!.focus();
      loadMoreButton()!.click();
      await harness.fixture.whenStable();

      const field = element().querySelector<HTMLInputElement>('.search__field')!;
      field.focus();

      expectSearch().flush(
        conversationPage([conversationFixture({ name: 'Last' })], {
          totalCount: LIBRARY_PAGE_SIZE + 1,
          pageSize: LIBRARY_PAGE_SIZE,
          skip: LIBRARY_PAGE_SIZE,
        }),
      );
      await harness.fixture.whenStable();

      expect(document.activeElement).toBe(field);
    });

    it('withholds the counter until there is one, and beside an error', async () => {
      await harness.navigateByUrl('/conversations', Conversations);
      expect(element().querySelector('.conversations__count')).toBeNull();

      expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
        status: 500,
        statusText: 'Internal Server Error',
      });
      await harness.fixture.whenStable();

      expect(element().querySelector('.conversations__count')).toBeNull();
    });
  });

  describe('project chip and row kebab', () => {
    const project = projectFixture({ name: 'Data Platform Migration' });

    /** Drains the root lookup store, which is what turns a projectId into a name. */
    function loadProjects(items = [project]): void {
      const lookup = TestBed.inject(ProjectLookupStore);
      lookup.ensureLoaded();
      TestBed.tick();
      backend
        .expectOne((request) => request.url === `${TEST_API_BASE_URL}/api/projects`)
        .flush(projectPage(items));
      TestBed.tick();
    }

    it('names the row’s project, and shows no chip for a standalone one', async () => {
      loadProjects();
      await open('/conversations', [
        conversationFixture({ projectId: project.id }),
        conversationFixture({ projectId: null }),
      ]);

      const chips = [...element().querySelectorAll('.conversations__project')];
      expect(chips).toHaveLength(1);
      expect(chips[0]?.textContent?.trim()).toBe('Data Platform Migration');
    });

    it('shows no chip for a project past the lookup ceiling', async () => {
      // Unnamable, and a dash would wrongly read as "none".
      loadProjects([]);
      await open('/conversations', [conversationFixture({ projectId: project.id })]);

      expect(element().querySelector('.conversations__project')).toBeNull();
    });

    it('offers the five row actions, routed through the shared store', async () => {
      loadProjects();
      const row = conversationFixture({ projectId: project.id });
      await open('/conversations', [row]);

      element().querySelector<HTMLButtonElement>('.menu__trigger')?.click();
      await harness.fixture.whenStable();

      const items = [...element().querySelectorAll<HTMLButtonElement>('[appMenuItem]')];
      expect(items.map((item) => item.textContent?.trim())).toEqual([
        'Rename',
        'Favourite',
        'Move to project',
        'Remove from project',
        'Delete',
      ]);
      // Never natively disabled: the attribute blurs the item the instant it is pressed
      // and strands the panel's roving focus.
      expect(items.some((item) => item.hasAttribute('disabled'))).toBe(false);

      items.find((item) => item.textContent?.includes('Remove from project'))?.click();
      await harness.fixture.whenStable();

      const request = backend.expectOne(`${TEST_API_BASE_URL}/api/conversations`);
      expect(request.request.body).toEqual({ id: row.id, name: row.name, projectId: null });
      request.flush({ ...row, projectId: null });
    });
  });

  it('puts both row controls beside the card title rather than across its foot', async () => {
    await open();

    setMediaQuery(NARROW_VIEWPORT, true);
    await harness.fixture.whenStable();
    resetMediaQueries();

    // Icon controls stretched across the card's foot would read as two broken bars.
    const trailing = element().querySelector('.card-row__trailing');
    expect(trailing?.querySelectorAll('button')).toHaveLength(2);
    expect(element().querySelector('.card-row__actions button')).toBeNull();
  });
});
