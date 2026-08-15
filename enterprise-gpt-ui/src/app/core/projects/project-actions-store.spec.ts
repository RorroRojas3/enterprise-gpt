import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Events } from '@ngrx/signals/events';
import { beforeEach, describe, expect, it } from 'vitest';
import { ProjectDto } from '@domain/api/project';
import { projectEvents } from '@core/events/project-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { projectDetailFixture, projectFixture } from '@testing/projects';
import { DUPLICATE_NAME_MESSAGE, projectNameMessages } from './duplicate-name';
import { ProjectActionsStore } from './project-actions-store';

const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

const DUPLICATE_NAME_PROBLEM = {
  ...PROBLEM_FIXTURES.validationError,
  instance: '/api/projects',
  errors: { Name: ["A project named 'RFP Responses' already exists."] },
};

describe('ProjectActionsStore (US-903)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ProjectActionsStore>;
  let toasts: InstanceType<typeof ToastStore>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ProjectActionsStore);
    toasts = TestBed.inject(ToastStore);
  });

  function expectPost(): TestRequest {
    return backend.expectOne(
      (request) => request.url === PROJECTS_URL && request.method === 'POST',
    );
  }

  function expectPut(id: string): TestRequest {
    return backend.expectOne(
      (request) => request.url === `${PROJECTS_URL}/${id}` && request.method === 'PUT',
    );
  }

  describe('create', () => {
    it('posts the whole body and is not applied until the server confirms', () => {
      const created = projectDetailFixture({ name: 'Helios' });
      let announced: ProjectDto | null = null;
      TestBed.inject(Events)
        .on(projectEvents.created)
        .subscribe(({ payload }) => (announced = payload));

      store.beginCreate();
      store.submitForm({ name: '  Helios  ', description: '', instructions: 'Cite Jira keys.' });
      TestBed.tick();

      const request = expectPost();
      // Trimmed, and the untouched optional field sent as null rather than as "" —
      // the column is nullable and an empty string is a different value.
      expect(request.request.body).toEqual({
        name: 'Helios',
        description: null,
        instructions: 'Cite Jira keys.',
      });
      // Nothing announced yet: creation mints a server id, so it cannot be optimistic.
      expect(announced).toBeNull();
      expect(store.formBusy()).toBe(true);

      request.flush(created);
      TestBed.tick();

      expect(announced).toEqual(created);
      expect(store.formMode()).toBeNull();
      expect(store.formBusy()).toBe(false);
    });

    it('renders a duplicate name inline and never as a toast', () => {
      store.beginCreate();
      store.submitForm({ name: 'RFP Responses', description: '', instructions: '' });
      TestBed.tick();

      expectPost().flush(DUPLICATE_NAME_PROBLEM, { status: 400, statusText: 'Bad Request' });
      TestBed.tick();

      // The dialog stays open with the message under the field, which is the whole
      // point: a toast would take the reader away from the input they must fix.
      expect(store.formMode()).toBe('create');
      expect(store.formRejectedName()).toBe('RFP Responses');
      expect(projectNameMessages(store.formError())).toEqual([DUPLICATE_NAME_MESSAGE]);
      expect(toasts.toasts()).toEqual([]);
    });

    it('degrades to a toast when the dialog was force-closed before the rejection landed', () => {
      store.beginCreate();
      store.submitForm({ name: 'RFP Responses', description: '', instructions: '' });
      TestBed.tick();

      store.cancelForm();
      expectPost().flush(DUPLICATE_NAME_PROBLEM, { status: 400, statusText: 'Bad Request' });
      TestBed.tick();

      // Nowhere left to render it inline, and a rejection nobody sees is a create that
      // silently failed.
      expect(toasts.toasts().length).toBe(1);
      expect(toasts.toasts()[0]?.detail).toBe(DUPLICATE_NAME_MESSAGE);
    });

    it('hands the created project back to the invoker that asked for it', () => {
      const created = projectDetailFixture();
      let handedBack: ProjectDto | null = null;

      store.beginCreate((project) => (handedBack = project));
      store.submitForm({ name: 'Helios', description: '', instructions: '' });
      TestBed.tick();
      expectPost().flush(created);
      TestBed.tick();

      expect(handedBack).toEqual(created);
    });
  });

  describe('edit', () => {
    it('fetches the project before opening on a listing row, so instructions survive', () => {
      const summary = projectFixture();
      const detail = projectDetailFixture({ ...summary, instructions: 'Cite Jira keys.' });

      store.beginEdit(summary);
      TestBed.tick();

      // The listing shape has no `instructions` key at all, so opening the form on it
      // would let a save PUT `instructions: null` over text the user never saw.
      const fetch = backend.expectOne(`${PROJECTS_URL}/${summary.id}`);
      expect(store.formMode()).toBe('edit');
      expect(store.formLoading()).toBe(true);

      fetch.flush(detail);
      TestBed.tick();

      expect(store.formLoading()).toBe(false);
      expect(store.formTarget()).toEqual(detail);
    });

    it('opens immediately when the caller already holds the full project', () => {
      const detail = projectDetailFixture({ instructions: 'Cite Jira keys.' });

      store.beginEdit(detail);
      TestBed.tick();

      backend.verify();
      expect(store.formLoading()).toBe(false);
      expect(store.formTarget()).toEqual(detail);
    });

    it('sends the whole representation, so a rename cannot clear the rest', () => {
      const detail = projectDetailFixture({
        name: 'Helios',
        description: 'Cutover work',
        instructions: 'Cite Jira keys.',
      });

      store.beginEdit(detail);
      store.submitForm({
        name: 'Helios 2.4',
        description: 'Cutover work',
        instructions: 'Cite Jira keys.',
      });
      TestBed.tick();

      const request = expectPut(detail.id);
      expect(request.request.body).toEqual({
        name: 'Helios 2.4',
        description: 'Cutover work',
        instructions: 'Cite Jira keys.',
      });

      request.flush({ ...detail, name: 'Helios 2.4' });
    });

    it('saves instructions alone by echoing the name and description back', () => {
      const detail = projectDetailFixture({
        name: 'Helios',
        description: 'Cutover work',
        instructions: 'Old rules.',
      });

      store.saveInstructions(detail, 'New rules.');
      TestBed.tick();

      const request = expectPut(detail.id);
      expect(request.request.body).toEqual({
        name: 'Helios',
        description: 'Cutover work',
        instructions: 'New rules.',
      });

      request.flush({ ...detail, instructions: 'New rules.' });
    });
  });

  describe('delete', () => {
    it('announces the deletion only once the server has confirmed it', () => {
      const target = projectFixture();
      let deletedId: string | null = null;
      TestBed.inject(Events)
        .on(projectEvents.deleted)
        .subscribe(({ payload }) => (deletedId = payload));

      store.beginDelete(target);
      store.confirmDelete();
      TestBed.tick();

      const request = backend.expectOne(
        (candidate) =>
          candidate.url === `${PROJECTS_URL}/${target.id}` && candidate.method === 'DELETE',
      );
      // Nothing removed optimistically: the same event releases every conversation the
      // project held, and undoing that would mean guessing which rows the server
      // reached.
      expect(deletedId).toBeNull();
      expect(store.isRowPending(target.id)).toBe(true);

      request.flush(null, { status: 204, statusText: 'No Content' });
      TestBed.tick();

      expect(deletedId).toBe(target.id);
      expect(store.isRowPending(target.id)).toBe(false);
    });

    it('refuses to reopen a dialog for a project whose own action is in flight', () => {
      const target = projectFixture();

      store.beginDelete(target);
      store.confirmDelete();
      TestBed.tick();

      const request = backend.expectOne(`${PROJECTS_URL}/${target.id}`);

      store.beginDelete(target);
      expect(store.deleteTarget()).toBeNull();

      request.flush(null, { status: 204, statusText: 'No Content' });
    });
  });
});
