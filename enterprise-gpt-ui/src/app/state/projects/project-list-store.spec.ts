import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ProjectSummaryDto } from '@shared/models/api/project.model';
import {
  DEFAULT_PROJECT_SORT,
  PROJECT_SORTS,
  ProjectSortOption,
} from '@shared/utils/navigation/projects-route';
import { projectEvents } from '@core/events/project-events';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { projectDetailFixture, projectFixture, projectPage } from '@testing/projects';
import { PROJECT_LIST_PAGE_SIZE, ProjectListStore } from './project-list-store';

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

describe('ProjectListStore', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ProjectListStore>;
  let term: ReturnType<typeof signal<string>>;
  let sort: ReturnType<typeof signal<ProjectSortOption>>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        ProjectListStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ProjectListStore);
    term = signal('');
    sort = signal(DEFAULT_PROJECT_SORT);
  });

  afterEach(() => {
    backend.verify();
  });

  /** Wires the query the way the screen's constructor does. */
  function bind(): void {
    TestBed.runInInjectionContext(() => store.bindQuery(() => ({ name: term(), sort: sort() })));
    TestBed.tick();
  }

  /** The option a test names, by its `?sort=` value. */
  function option(value: string): ProjectSortOption {
    const found = PROJECT_SORTS.find((candidate) => candidate.value === value);

    if (found === undefined) {
      throw new Error(`No sort option named ${value}.`);
    }

    return found;
  }

  function expectRequest(): TestRequest {
    return backend.expectOne((request) => request.url === PROJECTS_URL);
  }

  /** A full page, so more is left behind and `hasMore()` is true. */
  function fullPage(count = PROJECT_LIST_PAGE_SIZE): ProjectSummaryDto[] {
    return Array.from({ length: count }, () => projectFixture());
  }

  /** A first page that leaves more behind, answered the way the server would. */
  function loadFirstPage(totalCount = 60): void {
    bind();
    expectRequest().flush(
      projectPage(fullPage(), { totalCount, pageSize: PROJECT_LIST_PAGE_SIZE }),
    );
    TestBed.tick();
  }

  it('asks for the first page at the take the server clamps to', () => {
    bind();

    const request = expectRequest();
    expect(request.request.params.get('take')).toBe(String(PROJECT_LIST_PAGE_SIZE));
    expect(request.request.params.get('skip')).toBe('0');
    // Omitted, never sent empty: a blank `name=` still takes the server's LIKE branch.
    expect(request.request.params.has('name')).toBe(false);

    request.flush(projectPage([projectFixture()]));
  });

  it('sends the term as `name=` when the URL carries one', () => {
    term.set('helios');
    bind();

    const request = expectRequest();
    expect(request.request.params.get('name')).toBe('helios');

    request.flush(projectPage([projectFixture()]));
  });

  it('fetches only the first page, leaving the rest behind the control', () => {
    loadFirstPage(250);

    // One request until the reader asks for more.
    backend.verify();
    expect(store.entities().length).toBe(PROJECT_LIST_PAGE_SIZE);
    expect(store.hasMore()).toBe(true);
    expect(store.isFulfilled()).toBe(true);
  });

  it('appends the next page and moves the offset by what came back', () => {
    loadFirstPage(60);

    store.loadMore();
    TestBed.tick();

    const next = expectRequest();
    expect(next.request.params.get('skip')).toBe(String(PROJECT_LIST_PAGE_SIZE));
    expect(next.request.params.get('take')).toBe(String(PROJECT_LIST_PAGE_SIZE));
    next.flush(
      projectPage([projectFixture()], {
        totalCount: 60,
        pageSize: PROJECT_LIST_PAGE_SIZE,
        skip: PROJECT_LIST_PAGE_SIZE,
      }),
    );
    TestBed.tick();

    expect(store.entities().length).toBe(PROJECT_LIST_PAGE_SIZE + 1);
    expect(store.skip()).toBe(PROJECT_LIST_PAGE_SIZE + 1);
    expect(store.countSummary()).toBe('Showing 26 of 60 projects');
  });

  it('carries the term and the order onto an appended page', () => {
    term.set('helios');
    sort.set(option('name'));
    loadFirstPage(60);

    store.loadMore();
    TestBed.tick();

    const next = expectRequest();
    expect(next.request.params.get('name')).toBe('helios');
    expect(next.request.params.get('sort')).toBe('name');
    expect(next.request.params.get('dir')).toBe('asc');

    next.flush(projectPage([], { totalCount: 60, pageSize: PROJECT_LIST_PAGE_SIZE, skip: 25 }));
    TestBed.tick();
  });

  it('ignores a second press while a page is on the wire', () => {
    loadFirstPage();

    store.loadMore();
    TestBed.tick();
    const inFlight = expectRequest();
    expect(store.loadingMore()).toBe(true);

    store.loadMore();
    TestBed.tick();
    // `skip` has not moved yet, so a second request would fetch the same window twice.
    backend.expectNone(() => true);

    inFlight.flush(projectPage([], { totalCount: 60, pageSize: PROJECT_LIST_PAGE_SIZE, skip: 25 }));
    TestBed.tick();
    expect(store.loadingMore()).toBe(false);
  });

  it('refuses a press once everything is loaded', () => {
    loadFirstPage(PROJECT_LIST_PAGE_SIZE);

    expect(store.hasMore()).toBe(false);

    store.loadMore();
    TestBed.tick();

    backend.verify();
  });

  it('keeps the loaded cards when a page fails, and toasts instead', async () => {
    loadFirstPage();
    const toasts = TestBed.inject(ToastStore);

    store.loadMore();
    TestBed.tick();
    expectRequest().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    TestBed.tick();
    await Promise.resolve();

    expect(store.entities().length).toBe(PROJECT_LIST_PAGE_SIZE);
    expect(store.error()).toBeNull();
    expect(store.loadingMore()).toBe(false);
    expect(toasts.toasts()).toHaveLength(1);
  });

  it('drops a page whose result set a newer query replaced', () => {
    loadFirstPage(250);

    store.loadMore();
    TestBed.tick();
    const page = expectRequest();

    term.set('helios');
    TestBed.tick();

    // Otherwise it lands after the new query's `setAllEntities` and appends a stale window.
    expect(page.cancelled).toBe(true);
    expect(store.loadingMore()).toBe(false);

    const narrowed = expectRequest();
    expect(narrowed.request.params.get('name')).toBe('helios');
    expect(narrowed.request.params.get('skip')).toBe('0');
    narrowed.flush(
      projectPage([projectFixture({ name: 'Helios' })], { pageSize: PROJECT_LIST_PAGE_SIZE }),
    );
    TestBed.tick();

    expect(store.entities().length).toBe(1);
  });

  it('drops a page when a card is deleted out from under its offset', () => {
    loadFirstPage(250);
    const doomed = store.entities()[0];

    store.loadMore();
    TestBed.tick();
    const page = expectRequest();

    TestBed.inject(Dispatcher).dispatch(projectEvents.deleted(doomed?.id ?? ''));
    TestBed.tick();

    // Built on the pre-removal offset, it would skip the card that slid into the gap.
    expect(page.cancelled).toBe(true);
    expect(store.loadingMore()).toBe(false);
    expect(store.skip()).toBe(PROJECT_LIST_PAGE_SIZE - 1);
  });

  it('replaces the grid with an error and keeps Retry meaningful', () => {
    bind();

    expectRequest().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Server Error',
    });
    TestBed.tick();

    expect(store.error()).not.toBeNull();

    store.retry();
    TestBed.tick();

    const retried = expectRequest();
    expect(retried.request.params.get('skip')).toBe('0');
    retried.flush(projectPage([projectFixture()]));
  });

  describe('events', () => {
    it('puts a created project at the head and counts it', () => {
      bind();
      expectRequest().flush(projectPage([projectFixture()], { totalCount: 1 }));
      TestBed.tick();

      const created = projectDetailFixture({ name: 'Helios' });
      TestBed.inject(Dispatcher).dispatch(projectEvents.created(created));
      TestBed.tick();

      expect(store.entities()[0]?.id).toBe(created.id);
      expect(store.totalCount()).toBe(2);
      // The listing shape: `instructions` must not ride into a list entity.
      expect('instructions' in (store.entities()[0] ?? {})).toBe(false);
    });

    it('ignores a created project that does not match the active search', () => {
      term.set('helios');
      bind();
      expectRequest().flush(projectPage([], { totalCount: 0 }));
      TestBed.tick();

      TestBed.inject(Dispatcher).dispatch(
        projectEvents.created(projectDetailFixture({ name: 'Unrelated' })),
      );
      TestBed.tick();

      expect(store.entities()).toEqual([]);
    });

    it('drops a row renamed out of the active search', () => {
      term.set('helios');
      bind();
      const row = projectFixture({ name: 'Helios cutover' });
      expectRequest().flush(projectPage([row], { totalCount: 1 }));
      TestBed.tick();

      TestBed.inject(Dispatcher).dispatch(
        projectEvents.updated(projectDetailFixture({ ...row, name: 'Something else' })),
      );
      TestBed.tick();

      expect(store.entities()).toEqual([]);
      expect(store.totalCount()).toBe(0);
    });

    it('removes a deleted row and takes both counters down with it', () => {
      bind();
      const row = projectFixture();
      expectRequest().flush(projectPage([row, projectFixture()], { totalCount: 2 }));
      TestBed.tick();

      TestBed.inject(Dispatcher).dispatch(projectEvents.deleted(row.id));
      TestBed.tick();

      expect(store.entities().length).toBe(1);
      expect(store.totalCount()).toBe(1);
    });
  });

  describe('the order', () => {
    it('sends the default order even when the URL names none', () => {
      bind();

      const request = expectRequest();
      // Explicit: the grid defaults to "Last updated", but the bare endpoint answers
      // newest-created first.
      expect(request.request.params.get('sort')).toBe('updated');
      expect(request.request.params.get('dir')).toBe('desc');

      request.flush(projectPage([projectFixture()]));
    });

    it('sends the chosen order as the API pair', () => {
      sort.set(option('name'));
      bind();

      const request = expectRequest();
      expect(request.request.params.get('sort')).toBe('name');
      expect(request.request.params.get('dir')).toBe('asc');

      request.flush(projectPage([projectFixture()]));
    });

    it('carries the order onto an appended page, not only the first', () => {
      sort.set(option('favorites'));
      loadFirstPage(60);

      store.loadMore();
      TestBed.tick();

      // Without it the page would splice a differently-ordered run under the first.
      const second = expectRequest();
      expect(second.request.params.get('sort')).toBe('favorite');
      expect(second.request.params.get('dir')).toBe('desc');

      second.flush(projectPage([], { totalCount: 60, pageSize: PROJECT_LIST_PAGE_SIZE, skip: 25 }));
      TestBed.tick();
    });

    it('replaces the grid when the order changes rather than appending a second run', () => {
      bind();
      expectRequest().flush(projectPage([projectFixture(), projectFixture()], { totalCount: 2 }));
      TestBed.tick();

      sort.set(option('name'));
      TestBed.tick();

      const reordered = expectRequest();
      expect(reordered.request.params.get('sort')).toBe('name');
      reordered.flush(projectPage([projectFixture()], { totalCount: 1 }));
      TestBed.tick();

      expect(store.entities().length).toBe(1);
    });

    it('retries the order the cards on screen belong to', () => {
      sort.set(option('created'));
      bind();
      expectRequest().error(new ProgressEvent('error'));
      TestBed.tick();

      store.retry();
      TestBed.tick();

      const retried = expectRequest();
      expect(retried.request.params.get('sort')).toBe('created');
      retried.flush(projectPage([projectFixture()]));
    });

    it('leaves a starred card where it is, rather than re-draining to move one row', () => {
      // A star does change the server's order here, but refetching would rearrange the grid
      // under the cursor that pressed it.
      sort.set(option('favorites'));
      bind();
      const first = projectFixture({ isFavorite: true });
      const second = projectFixture({ isFavorite: false });
      expectRequest().flush(projectPage([first, second], { totalCount: 2 }));
      TestBed.tick();

      TestBed.inject(Dispatcher).dispatch(
        projectEvents.favorited({ id: second.id, isFavorite: true }),
      );
      TestBed.tick();

      expect(store.entities().map((project) => project.id)).toEqual([first.id, second.id]);
      expect(store.entities()[1]?.isFavorite).toBe(true);
    });
  });

  it('cancels an appended page on sign-out and empties', () => {
    loadFirstPage(250);

    store.loadMore();
    TestBed.tick();
    const inFlight = expectRequest();

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    TestBed.tick();

    // The reset cancels nothing, so the response would write the previous user's cards back.
    expect(inFlight.cancelled).toBe(true);
    expect(store.entities()).toEqual([]);
    expect(store.loadingMore()).toBe(false);
  });
});
