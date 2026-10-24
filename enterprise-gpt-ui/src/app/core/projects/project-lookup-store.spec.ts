import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ProjectSummaryDto } from '@core/api/project.model';
import { projectEvents } from '@core/events/project-events';
import { sessionEvents } from '@core/events/session-events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { projectDetailFixture, projectFixture, projectPage } from '@testing/projects';
import { PROJECT_LOAD_CEILING, PROJECT_PAGE_SIZE } from './project-load';
import { ProjectLookupStore } from './project-lookup-store';

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

describe('ProjectLookupStore', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ProjectLookupStore>;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ProjectLookupStore);
  });

  afterEach(() => {
    backend.verify();
  });

  function flush(): void {
    TestBed.tick();
  }

  function expectRequest(): TestRequest {
    return backend.expectOne((request) => request.url === PROJECTS_URL);
  }

  /** A full page, so the drain asks for another. */
  function fullPage(count = PROJECT_PAGE_SIZE): ProjectSummaryDto[] {
    return Array.from({ length: count }, () => projectFixture());
  }

  function load(items: ProjectSummaryDto[]): void {
    store.ensureLoaded();
    flush();
    expectRequest().flush(projectPage(items));
    flush();
  }

  it('requests nothing until the bootstrap releases it', () => {
    flush();

    backend.expectNone((request) => request.url === PROJECTS_URL);
  });

  it('asks for the first page at the take the server clamps to', () => {
    store.ensureLoaded();
    flush();

    const request = expectRequest();
    expect(request.request.params.get('take')).toBe(String(PROJECT_PAGE_SIZE));
    expect(request.request.params.get('skip')).toBe('0');
    // No term ever: this list is not filtered on the server, unlike the grid's.
    expect(request.request.params.has('name')).toBe(false);
    // Starred first, which keeps the sidebar's favourites complete within the ceiling.
    expect(request.request.params.get('sort')).toBe('favorite');
    expect(request.request.params.get('dir')).toBe('desc');

    request.flush(projectPage([projectFixture()]));
    flush();
  });

  it('releases the drain only once, however often ensureLoaded is called', () => {
    store.ensureLoaded();
    store.ensureLoaded();
    flush();

    expectRequest().flush(projectPage([projectFixture()]));
    flush();
    store.ensureLoaded();
    flush();

    backend.expectNone((request) => request.url === PROJECTS_URL);
  });

  it('drains every page of a multi-page set', () => {
    store.ensureLoaded();
    flush();

    expectRequest().flush(projectPage(fullPage(), { totalCount: PROJECT_PAGE_SIZE + 2 }));
    flush();

    const second = expectRequest();
    expect(second.request.params.get('skip')).toBe(String(PROJECT_PAGE_SIZE));
    // On every page: the server orders each request independently.
    expect(second.request.params.get('sort')).toBe('favorite');
    expect(second.request.params.get('dir')).toBe('desc');
    second.flush(
      projectPage([projectFixture(), projectFixture()], {
        totalCount: PROJECT_PAGE_SIZE + 2,
        skip: PROJECT_PAGE_SIZE,
      }),
    );
    flush();

    expect(store.projects()).toHaveLength(PROJECT_PAGE_SIZE + 2);
    expect(store.truncated()).toBe(false);
  });

  it('stops at the ceiling and says so', () => {
    store.ensureLoaded();
    flush();

    const total = PROJECT_LOAD_CEILING + 12;
    for (let skip = 0; skip < PROJECT_LOAD_CEILING; skip += PROJECT_PAGE_SIZE) {
      expectRequest().flush(projectPage(fullPage(), { totalCount: total, skip }));
      flush();
    }

    backend.expectNone((request) => request.url === PROJECTS_URL);
    expect(store.projects()).toHaveLength(PROJECT_LOAD_CEILING);
    expect(store.truncated()).toBe(true);
    expect(store.ceilingNotice()).toBe(
      `Showing the first ${PROJECT_LOAD_CEILING} of ${total} projects.`,
    );
  });

  it('resolves a project id to its name, and an unknown id to null', () => {
    const project = projectFixture({ name: 'Data Platform Migration' });
    load([project]);

    expect(store.nameOf()(project.id)).toBe('Data Platform Migration');
    expect(store.nameOf()('not-loaded')).toBeNull();
    expect(store.nameOf()(null)).toBeNull();
  });

  it('records an error the first page could not get past, and retries from the start', () => {
    store.ensureLoaded();
    flush();

    expectRequest().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    flush();

    expect(store.error()).not.toBeNull();
    expect(store.projects()).toHaveLength(0);

    store.retry();
    flush();
    expectRequest().flush(projectPage([projectFixture()]));
    flush();

    expect(store.error()).toBeNull();
    expect(store.projects()).toHaveLength(1);
  });

  it('keeps the pages already drained when a later one fails', () => {
    store.ensureLoaded();
    flush();

    expectRequest().flush(projectPage(fullPage(), { totalCount: PROJECT_PAGE_SIZE + 5 }));
    flush();

    expectRequest().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    flush();

    expect(store.error()).not.toBeNull();
    expect(store.projects()).toHaveLength(PROJECT_PAGE_SIZE);
  });

  describe('events', () => {
    it('puts a created project at the head, where a refetch would', () => {
      const existing = projectFixture();
      load([existing]);
      const created = projectDetailFixture({ name: 'Q3 Board Deck' });

      TestBed.inject(Dispatcher).dispatch(projectEvents.created(created));
      flush();

      // The picker's "New project" needs the row to exist before its continuation runs.
      expect(store.projects().map((p) => p.id)).toEqual([created.id, existing.id]);
      expect(store.nameOf()(created.id)).toBe('Q3 Board Deck');
    });

    it('adopts a rename without evicting the row', () => {
      const project = projectFixture({ name: 'Old' });
      load([project]);

      TestBed.inject(Dispatcher).dispatch(
        projectEvents.updated(projectDetailFixture({ ...project, name: 'New' })),
      );
      flush();

      expect(store.projects()).toHaveLength(1);
      expect(store.nameOf()(project.id)).toBe('New');
    });

    it('drops a deleted project so the picker cannot offer it', () => {
      const project = projectFixture();
      load([project, projectFixture()]);

      TestBed.inject(Dispatcher).dispatch(projectEvents.deleted(project.id));
      flush();

      expect(store.projects()).toHaveLength(1);
      expect(store.nameOf()(project.id)).toBeNull();
    });
  });

  it('exposes the starred projects in the server order, and only those', () => {
    const starred = projectFixture({ name: 'Helios', isFavorite: true });
    const plain = projectFixture({ name: 'Hiring' });
    const alsoStarred = projectFixture({ name: 'RFP', isFavorite: true });
    load([starred, plain, alsoStarred]);

    expect(store.hasFavorites()).toBe(true);
    expect(store.favorites().map((project) => project.name)).toEqual(['Helios', 'RFP']);
  });

  it('reports no favourites when nothing is starred, so the sidebar section stays absent', () => {
    load([projectFixture(), projectFixture()]);

    expect(store.hasFavorites()).toBe(false);
    expect(store.favorites()).toHaveLength(0);
  });

  it('moves a row into and out of the favourites on a server-confirmed toggle', () => {
    const project = projectFixture({ name: 'Helios' });
    load([project, projectFixture()]);

    TestBed.inject(Dispatcher).dispatch(
      projectEvents.favorited({ id: project.id, isFavorite: true }),
    );
    flush();

    expect(store.favorites().map((favorite) => favorite.id)).toEqual([project.id]);

    TestBed.inject(Dispatcher).dispatch(
      projectEvents.favorited({ id: project.id, isFavorite: false }),
    );
    flush();

    expect(store.favorites()).toHaveLength(0);
  });

  it('ignores a favourite for a project it does not hold', () => {
    // An id past the drain ceiling, or one belonging to a list this store never read.
    load([projectFixture()]);

    TestBed.inject(Dispatcher).dispatch(
      projectEvents.favorited({ id: 'not-a-loaded-project', isFavorite: true }),
    );
    flush();

    expect(store.projects()).toHaveLength(1);
    expect(store.favorites()).toHaveLength(0);
  });

  it('empties for the next user on sign-out', () => {
    load([projectFixture()]);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    flush();

    expect(store.projects()).toHaveLength(0);
  });

  it('cancels a drain in flight when the user signs out', () => {
    // `withResetOnSignOut` clears state but cancels no work; without `takeUntil` this page
    // would land as the next user's list.
    store.ensureLoaded();
    flush();
    const request = expectRequest();

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    flush();

    expect(request.cancelled).toBe(true);
    expect(store.projects()).toHaveLength(0);
  });
});
