import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ProjectSummaryDto } from '@domain/api/project';
import { projectEvents } from '@core/events/project-events';
import { sessionEvents } from '@core/events/session-events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { projectDetailFixture, projectFixture, projectPage } from '@testing/projects';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { PROJECT_LOAD_CEILING, PROJECT_PAGE_SIZE } from './project-load';
import { ProjectLookupStore } from './project-lookup-store';

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

describe('ProjectLookupStore (US-307)', () => {
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
    // The sentinel is what keeps the request from going out before a token exists —
    // `SessionBootstrap` calls `ensureLoaded` only after `POST api/users/me` resolves.
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
      `Showing the ${PROJECT_LOAD_CEILING} most recent of ${total} projects.`,
    );
  });

  it('resolves a project id to its name, and an unknown id to null', () => {
    // The chip's whole contract: `ConversationDto` carries the id and no name, and a
    // project past the ceiling has to read as "cannot name it", never as "none".
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
    // Unlike the grid, which replaces itself with an error panel: this is a lookup, and
    // a chip that can name the first hundred projects beats one that names none. The
    // picker is what hides the partial list behind the error.
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

      // The head, because the server orders by dateCreated descending — and the
      // picker's "New project" depends on the row existing before its continuation runs.
      expect(store.projects().map((p) => p.id)).toEqual([created.id, existing.id]);
      expect(store.nameOf()(created.id)).toBe('Q3 Board Deck');
    });

    it('adopts a rename without evicting the row', () => {
      // No term filters this list, so unlike the grid a rename can never drop a project
      // out of it — which is what keeps the chip readable after an edit.
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

  it('empties for the next user on sign-out', () => {
    load([projectFixture()]);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    flush();

    expect(store.projects()).toHaveLength(0);
  });

  it('cancels a drain in flight when the user signs out', () => {
    // `withResetOnSignOut` clears state and does not cancel work: without the
    // takeUntil, this page would land on the emptied store as the next user's list.
    store.ensureLoaded();
    flush();
    const request = expectRequest();

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    flush();

    expect(request.cancelled).toBe(true);
    expect(store.projects()).toHaveLength(0);
  });
});
