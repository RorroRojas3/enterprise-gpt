import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { projectDocumentFixture } from '@testing/projects';
import { ProjectDocumentsStore } from './project-documents-store';

const PROJECT_ID = '11111111-5555-4666-8777-888888888888';
const DOCUMENTS_URL = `${TEST_API_BASE_URL}/api/projects/${PROJECT_ID}/documents`;

describe('ProjectDocumentsStore (US-905)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ProjectDocumentsStore>;
  let projectId: ReturnType<typeof signal<string | null>>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        ProjectDocumentsStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ProjectDocumentsStore);
    projectId = signal<string | null>(PROJECT_ID);
  });

  afterEach(() => {
    backend.verify();
  });

  function bind(): void {
    TestBed.runInInjectionContext(() => store.bindProject(projectId));
    TestBed.tick();
  }

  function expectList(): TestRequest {
    return backend.expectOne((request) => request.url === DOCUMENTS_URL);
  }

  it('reads the bare array the route answers with — no envelope, no paging', () => {
    bind();

    const rows = [projectDocumentFixture(), projectDocumentFixture()];
    const request = expectList();
    expect(request.request.params.keys()).toEqual([]);

    request.flush(rows);
    TestBed.tick();

    expect(store.documents()).toEqual(rows);
    expect(store.count()).toBe(2);
  });

  it('removes a row at once and puts it back at its index on a failure', () => {
    bind();
    const first = projectDocumentFixture();
    const target = projectDocumentFixture();
    const last = projectDocumentFixture();
    expectList().flush([first, target, last]);
    TestBed.tick();

    store.remove(target);
    TestBed.tick();

    expect(store.documents().map((row) => row.id)).toEqual([first.id, last.id]);
    expect(store.isRowPending(target.id)).toBe(true);

    backend
      .expectOne(`${DOCUMENTS_URL}/${target.id}`)
      .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
        status: 500,
        statusText: 'Server Error',
      });
    TestBed.tick();

    // Back where it was, not at either end: the reader's mental model of the list is
    // its order.
    expect(store.documents().map((row) => row.id)).toEqual([first.id, target.id, last.id]);
    expect(store.isRowPending(target.id)).toBe(false);
  });

  it('keeps the removal when the server confirms it', () => {
    bind();
    const target = projectDocumentFixture();
    expectList().flush([target]);
    TestBed.tick();

    store.remove(target);
    TestBed.tick();
    backend
      .expectOne(`${DOCUMENTS_URL}/${target.id}`)
      .flush(null, { status: 204, statusText: 'No Content' });
    TestBed.tick();

    expect(store.documents()).toEqual([]);
  });

  it('refuses a second removal of a row already in flight', () => {
    bind();
    const target = projectDocumentFixture();
    expectList().flush([target]);
    TestBed.tick();

    store.remove(target);
    TestBed.tick();
    const request = backend.expectOne(`${DOCUMENTS_URL}/${target.id}`);

    store.remove(target);
    TestBed.tick();

    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('re-reads on refresh, which is how a finished upload becomes a row', () => {
    bind();
    expectList().flush([]);
    TestBed.tick();

    store.refresh();
    TestBed.tick();

    // The upload route answers with a job id, never a document, so the name, size and
    // created date can only come from asking for the list again.
    expectList().flush([projectDocumentFixture()]);
    TestBed.tick();

    expect(store.count()).toBe(1);
  });

  it('does not let a previous project’s response land in the new one’s list', () => {
    bind();
    const stale = expectList();

    const other = '22222222-5555-4666-8777-888888888888';
    projectId.set(other);
    TestBed.tick();

    expect(stale.cancelled).toBe(true);
    backend
      .expectOne(`${TEST_API_BASE_URL}/api/projects/${other}/documents`)
      .flush([projectDocumentFixture()]);
    TestBed.tick();

    expect(store.projectId()).toBe(other);
  });
});
