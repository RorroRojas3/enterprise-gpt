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
import { ProjectSummaryDto } from '@domain/api/project';
import { projectEvents } from '@core/events/project-events';
import { sessionEvents } from '@core/events/session-events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { projectDetailFixture, projectFixture, projectPage } from '@testing/projects';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { PROJECT_LOAD_CEILING, PROJECT_PAGE_SIZE, ProjectListStore } from './project-list-store';

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

describe('ProjectListStore (US-901)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ProjectListStore>;
  let term: ReturnType<typeof signal<string>>;

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
  });

  afterEach(() => {
    backend.verify();
  });

  /** Wires the query the way the screen's constructor does. */
  function bind(): void {
    TestBed.runInInjectionContext(() => store.bindQuery(() => ({ name: term() })));
    TestBed.tick();
  }

  function expectRequest(): TestRequest {
    return backend.expectOne((request) => request.url === PROJECTS_URL);
  }

  /** A full page, so the drain asks for another. */
  function fullPage(count = PROJECT_PAGE_SIZE): ProjectSummaryDto[] {
    return Array.from({ length: count }, () => projectFixture());
  }

  it('asks for the first page at the take the server clamps to', () => {
    bind();

    const request = expectRequest();
    expect(request.request.params.get('take')).toBe(String(PROJECT_PAGE_SIZE));
    expect(request.request.params.get('skip')).toBe('0');
    // Omitted, never sent empty: a blank `name=` still takes the server down its LIKE
    // branch.
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

  it('drains every page of a result set larger than one page', () => {
    bind();

    expectRequest().flush(projectPage(fullPage(), { totalCount: 250 }));
    TestBed.tick();

    const second = expectRequest();
    expect(second.request.params.get('skip')).toBe(String(PROJECT_PAGE_SIZE));
    second.flush(projectPage(fullPage(), { totalCount: 250, skip: PROJECT_PAGE_SIZE }));
    TestBed.tick();

    const third = expectRequest();
    expect(third.request.params.get('skip')).toBe(String(PROJECT_PAGE_SIZE * 2));
    third.flush(projectPage(fullPage(50), { totalCount: 250, skip: PROJECT_PAGE_SIZE * 2 }));
    TestBed.tick();

    expect(store.entities().length).toBe(250);
    expect(store.truncated()).toBe(false);
    expect(store.isFulfilled()).toBe(true);
  });

  it('stops at the ceiling and says so, rather than draining a whole tenant', () => {
    bind();

    for (let page = 0; page * PROJECT_PAGE_SIZE < PROJECT_LOAD_CEILING; page++) {
      expectRequest().flush(
        projectPage(fullPage(), { totalCount: 1284, skip: page * PROJECT_PAGE_SIZE }),
      );
      TestBed.tick();
    }

    // No sixth request: the ceiling is the whole point.
    backend.verify();
    expect(store.entities().length).toBe(PROJECT_LOAD_CEILING);
    expect(store.truncated()).toBe(true);
    expect(store.ceilingNotice()).toBe('Showing the 500 most recent of 1284 projects.');
  });

  it('supersedes an in-flight drain rather than letting its pages land', () => {
    bind();

    const first = expectRequest();
    first.flush(projectPage(fullPage(), { totalCount: 250 }));
    TestBed.tick();

    const secondPage = expectRequest();

    term.set('helios');
    TestBed.tick();

    expect(secondPage.cancelled).toBe(true);

    const narrowed = expectRequest();
    expect(narrowed.request.params.get('name')).toBe('helios');
    expect(narrowed.request.params.get('skip')).toBe('0');
    narrowed.flush(projectPage([projectFixture({ name: 'Helios' })]));
    TestBed.tick();

    expect(store.entities().length).toBe(1);
    expect(store.truncated()).toBe(false);
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
      // The listing shape, not the detail one: `instructions` must not ride into a
      // list entity, where the next reader would take it for authoritative.
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

  it('cancels its drain on sign-out and empties', () => {
    bind();
    expectRequest().flush(projectPage(fullPage(), { totalCount: 250 }));
    TestBed.tick();

    const inFlight = expectRequest();
    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    TestBed.tick();

    expect(inFlight.cancelled).toBe(true);
    expect(store.entities()).toEqual([]);
    expect(store.truncated()).toBe(false);
  });
});
