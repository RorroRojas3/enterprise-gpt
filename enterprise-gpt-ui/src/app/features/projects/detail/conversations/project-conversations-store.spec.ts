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
import { ConversationDto } from '@domain/api/conversation';
import { conversationEvents } from '@core/events/conversation-events';
import { sessionEvents } from '@core/events/session-events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import {
  PROJECT_CONVERSATIONS_PAGE_SIZE,
  ProjectConversationsStore,
} from './project-conversations-store';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;
const PROJECT_ID = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';

describe('ProjectConversationsStore (US-908)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ProjectConversationsStore>;
  let projectId: ReturnType<typeof signal<string | null>>;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        ProjectConversationsStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ProjectConversationsStore);
    projectId = signal<string | null>(PROJECT_ID);
  });

  afterEach(() => {
    backend.verify();
  });

  function flush(): void {
    TestBed.tick();
  }

  /** Wires the route the way `ProjectDetail`'s constructor does. */
  function bind(): void {
    TestBed.runInInjectionContext(() => store.bindProject(projectId));
    flush();
  }

  function expectSearch(): TestRequest {
    return backend.expectOne((request) => request.url === SEARCH_URL);
  }

  function load(items: ConversationDto[], totalCount = items.length): void {
    bind();
    expectSearch().flush(
      conversationPage(items, { totalCount, pageSize: PROJECT_CONVERSATIONS_PAGE_SIZE }),
    );
    flush();
  }

  it('issues one filtered request, with no drain and no ceiling', () => {
    // US-908's second criterion. The first describes an interim client-side drain to a
    // 500-item ceiling; US-907 landed with this story, so that behaviour never shipped.
    bind();

    const request = expectSearch();
    expect(request.request.params.get('projectId')).toBe(PROJECT_ID);
    expect(request.request.params.get('skip')).toBe('0');
    expect(request.request.params.get('take')).toBe(String(PROJECT_CONVERSATIONS_PAGE_SIZE));

    request.flush(conversationPage([conversationFixture()]));
    flush();

    backend.expectNone((r) => r.url === SEARCH_URL);
  });

  it('requests nothing until the route resolves a project', () => {
    projectId.set(null);
    bind();

    backend.expectNone((request) => request.url === SEARCH_URL);
  });

  it('withholds the tab count until the list has been read', () => {
    // "Conversations 0" on a project that has four is a worse answer than no count.
    bind();
    expect(store.count()).toBeNull();

    expectSearch().flush(conversationPage([conversationFixture()], { totalCount: 4 }));
    flush();

    expect(store.count()).toBe(4);
  });

  it('appends a page rather than replacing the rows', () => {
    const first = Array.from({ length: PROJECT_CONVERSATIONS_PAGE_SIZE }, () =>
      conversationFixture(),
    );
    load(first, PROJECT_CONVERSATIONS_PAGE_SIZE + 1);

    store.loadMore();
    flush();

    const page = expectSearch();
    expect(page.request.params.get('skip')).toBe(String(PROJECT_CONVERSATIONS_PAGE_SIZE));
    expect(page.request.params.get('projectId')).toBe(PROJECT_ID);
    page.flush(
      conversationPage([conversationFixture()], {
        totalCount: PROJECT_CONVERSATIONS_PAGE_SIZE + 1,
        pageSize: PROJECT_CONVERSATIONS_PAGE_SIZE,
        skip: PROJECT_CONVERSATIONS_PAGE_SIZE,
      }),
    );
    flush();

    expect(store.entities()).toHaveLength(PROJECT_CONVERSATIONS_PAGE_SIZE + 1);
    expect(store.hasMore()).toBe(false);
  });

  it('keeps the loaded rows when a page fails, rather than showing an error panel', () => {
    load(
      Array.from({ length: PROJECT_CONVERSATIONS_PAGE_SIZE }, () => conversationFixture()),
      60,
    );

    store.loadMore();
    flush();
    expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    flush();

    expect(store.entities()).toHaveLength(PROJECT_CONVERSATIONS_PAGE_SIZE);
    expect(store.error()).toBeNull();
    expect(store.loadingMore()).toBe(false);
  });

  it('replaces the rows when the reader moves to another project', () => {
    load([conversationFixture()]);
    const other = 'bbbbbbbb-cccc-4ddd-8eee-ffffffffffff';

    projectId.set(other);
    flush();

    const request = expectSearch();
    expect(request.request.params.get('projectId')).toBe(other);
    request.flush(conversationPage([conversationFixture(), conversationFixture()]));
    flush();

    expect(store.entities()).toHaveLength(2);
    expect(store.projectId()).toBe(other);
  });

  describe('events', () => {
    it('drops a row moved to another project — including from this panel’s own kebab', () => {
      const row = conversationFixture({ projectId: PROJECT_ID });
      load([row, conversationFixture({ projectId: PROJECT_ID })], 2);

      TestBed.inject(Dispatcher).dispatch(
        conversationEvents.updated({ ...row, projectId: 'somewhere-else' }),
      );
      flush();

      expect(store.entities().map((c) => c.id)).not.toContain(row.id);
      expect(store.totalCount()).toBe(1);
    });

    it('drops a row removed from every project', () => {
      const row = conversationFixture({ projectId: PROJECT_ID });
      load([row], 1);

      TestBed.inject(Dispatcher).dispatch(conversationEvents.updated({ ...row, projectId: null }));
      flush();

      expect(store.entities()).toHaveLength(0);
      expect(store.totalCount()).toBe(0);
    });

    it('adopts a rename or a favourite in place', () => {
      const row = conversationFixture({ projectId: PROJECT_ID, isFavorite: false });
      load([row]);

      TestBed.inject(Dispatcher).dispatch(
        conversationEvents.updated({ ...row, name: 'Renamed', isFavorite: true }),
      );
      flush();

      expect(store.entities()[0]?.name).toBe('Renamed');
      expect(store.entities()[0]?.isFavorite).toBe(true);
    });

    it('drops a deleted row and shifts the counter', () => {
      const row = conversationFixture({ projectId: PROJECT_ID });
      load([row, conversationFixture({ projectId: PROJECT_ID })], 2);

      TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(row.id));
      flush();

      expect(store.entities()).toHaveLength(1);
      expect(store.totalCount()).toBe(1);
    });

    it('ignores an event for a row it does not hold', () => {
      load([conversationFixture({ projectId: PROJECT_ID })], 1);

      TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted('not-here'));
      flush();

      expect(store.entities()).toHaveLength(1);
      expect(store.totalCount()).toBe(1);
    });

    it('re-reads when a conversation is moved *into* this project from elsewhere', () => {
      // Reachable with both surfaces on screen: the sidebar row kebab opens the same
      // picker while this screen is up. Without this the panel and the tab count would
      // under-report until the reader left the route and came back.
      load([conversationFixture({ projectId: PROJECT_ID })], 1);
      const arriving = conversationFixture({ projectId: PROJECT_ID });

      TestBed.inject(Dispatcher).dispatch(conversationEvents.updated(arriving));
      flush();

      const refetch = expectSearch();
      expect(refetch.request.params.get('projectId')).toBe(PROJECT_ID);
      // The loaded rows stay on screen while it flies — `isFirstLoad` is false with
      // entities in hand, so the panel does not blank.
      expect(store.entities()).toHaveLength(1);

      refetch.flush(
        conversationPage([arriving, ...store.entities()], {
          totalCount: 2,
          pageSize: PROJECT_CONVERSATIONS_PAGE_SIZE,
        }),
      );
      flush();

      expect(store.entities()).toHaveLength(2);
      expect(store.count()).toBe(2);
    });

    it('ignores a conversation moved between two other projects', () => {
      load([conversationFixture({ projectId: PROJECT_ID })], 1);

      TestBed.inject(Dispatcher).dispatch(
        conversationEvents.updated(conversationFixture({ projectId: 'elsewhere' })),
      );
      flush();

      backend.expectNone((request) => request.url === SEARCH_URL);
      expect(store.entities()).toHaveLength(1);
    });
  });

  it('cancels the request in flight when the user signs out', () => {
    // `withResetOnSignOut` clears state and does not cancel work: without the takeUntil
    // this response would land on the emptied store as the next user's list.
    bind();
    const request = expectSearch();

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    flush();

    expect(request.cancelled).toBe(true);
    expect(store.entities()).toHaveLength(0);
  });
});
