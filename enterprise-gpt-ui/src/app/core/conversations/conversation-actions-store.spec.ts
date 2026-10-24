import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { Subscription } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConversationDto } from '@core/api/conversation.model';
import { conversationEvents } from '@core/events/conversation-events';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ConversationActionsStore } from './conversation-actions-store';
import { ConversationListStore } from './conversation-list-store';

const PUT_URL = `${TEST_API_BASE_URL}/api/conversations`;
const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

describe('ConversationActionsStore', () => {
  let store: InstanceType<typeof ConversationActionsStore>;
  let list: InstanceType<typeof ConversationListStore>;
  let backend: HttpTestingController;

  let updates: ConversationDto[];
  let deletions: string[];
  let eventsSubscription: Subscription;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    store = TestBed.inject(ConversationActionsStore);
    list = TestBed.inject(ConversationListStore);
    backend = TestBed.inject(HttpTestingController);

    updates = [];
    deletions = [];
    // `Events` is platform-scoped and outlives resetTestingModule; afterEach unsubscribes.
    const events = TestBed.inject(Events);
    eventsSubscription = events
      .on(conversationEvents.updated)
      .subscribe(({ payload }) => updates.push(payload));
    eventsSubscription.add(
      events.on(conversationEvents.deleted).subscribe(({ payload }) => deletions.push(payload)),
    );
  });

  afterEach(() => {
    eventsSubscription.unsubscribe();
    backend.verify();
  });

  function flush(): void {
    TestBed.tick();
  }

  function signOut(): void {
    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
  }

  function loadList(items: ConversationDto[]): void {
    list.ensureLoaded();
    flush();
    backend.expectOne((request) => request.url === SEARCH_URL).flush(conversationPage(items));
    flush();
  }

  function expectPut(): TestRequest {
    return backend.expectOne(PUT_URL);
  }

  describe('rename', () => {
    it('sends the full-representation body with the current projectId echoed, trimmed', () => {
      // The PUT replaces the whole representation; omitting projectId would unlink it.
      const target = conversationFixture({
        projectId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
      });
      loadList([target]);

      store.beginRename(target);
      store.submitRename('  Renamed  ');

      const request = expectPut();
      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({
        id: target.id,
        name: 'Renamed',
        projectId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
      });

      request.flush({ ...target, name: 'Renamed' });
    });

    it('shows the new name optimistically and marks the row pending while in flight', () => {
      const target = conversationFixture({ name: 'Before' });
      loadList([target]);

      store.beginRename(target);
      store.submitRename('After');

      expect(list.entities()[0]?.name).toBe('After');
      expect(list.isRowPending(target.id)).toBe(true);
      expect(store.renameBusy()).toBe(true);

      expectPut().flush({ ...target, name: 'After' });
    });

    it('adopts the server DTO, closes the dialog and announces the update on success', () => {
      const target = conversationFixture({ name: 'Before' });
      loadList([target]);

      store.beginRename(target);
      store.submitRename('After');

      const fromServer = {
        ...target,
        name: 'After',
        dateModified: '2026-08-11T12:34:56+00:00',
      };
      expectPut().flush(fromServer);

      expect(list.entities()[0]?.dateModified).toBe('2026-08-11T12:34:56+00:00');
      expect(store.renameTarget()).toBeNull();
      expect(store.renameBusy()).toBe(false);
      expect(list.isRowPending(target.id)).toBe(false);
      expect(updates).toEqual([fromServer]);
    });

    it('rolls back, stays open and holds the message inline on a validation rejection', () => {
      const target = conversationFixture({ name: 'Before' });
      loadList([target]);
      const toasts = TestBed.inject(ToastStore);

      store.beginRename(target);
      store.submitRename('After');
      expectPut().flush(PROBLEM_FIXTURES.validationError, {
        status: 400,
        statusText: 'Bad Request',
      });

      expect(list.entities()[0]?.name).toBe('Before');
      expect(store.renameTarget()).toEqual(target);
      expect(store.renameError()?.kind).toBe('validation-error');
      expect(store.renameRejectedName()).toBe('After');
      expect(list.isRowPending(target.id)).toBe(false);
      // Validation is inline-only, never toasted.
      expect(toasts.assertiveToasts()).toHaveLength(0);
      expect(updates).toHaveLength(0);
    });

    it('rolls back, closes and raises a toast on any other failure', () => {
      const target = conversationFixture({ name: 'Before' });
      loadList([target]);
      const toasts = TestBed.inject(ToastStore);

      store.beginRename(target);
      store.submitRename('After');
      expectPut().flush('', { status: 503, statusText: 'Service Unavailable' });

      expect(list.entities()[0]?.name).toBe('Before');
      expect(store.renameTarget()).toBeNull();
      expect(toasts.assertiveToasts()).toHaveLength(1);
      expect(updates).toHaveLength(0);
    });

    it('treats an unchanged name as a close, not a request', () => {
      const target = conversationFixture({ name: 'Same' });
      loadList([target]);

      store.beginRename(target);
      store.submitRename('  Same  ');

      backend.expectNone(PUT_URL);
      expect(store.renameTarget()).toBeNull();
    });

    it('ignores an empty name and keeps the dialog open', () => {
      const target = conversationFixture();
      loadList([target]);

      store.beginRename(target);
      store.submitRename('   ');

      backend.expectNone(PUT_URL);
      expect(store.renameTarget()).toEqual(target);
    });

    it('issues one request for a double submit', () => {
      const target = conversationFixture();
      loadList([target]);

      store.beginRename(target);
      store.submitRename('First');
      store.submitRename('Second');

      const request = expectPut();
      expect((request.request.body as { name: string }).name).toBe('First');
      request.flush({ ...target, name: 'First' });
    });

    it('drops the response and resets when the user signs out mid-rename', () => {
      const target = conversationFixture();
      loadList([target]);
      const toasts = TestBed.inject(ToastStore);

      store.beginRename(target);
      store.submitRename('After');
      const request = expectPut();

      signOut();

      expect(request.cancelled).toBe(true);
      expect(store.renameTarget()).toBeNull();
      expect(store.renameBusy()).toBe(false);
      // finalize runs on cancellation too, or the id would stay pending for the next user.
      expect(list.isRowPending(target.id)).toBe(false);
      expect(toasts.assertiveToasts()).toHaveLength(0);
    });

    it('degrades a validation rejection to a toast when the dialog closed mid-flight', () => {
      // A second Escape is uncancelable in Chromium, so a busy dialog can be force-closed
      // with the PUT still on the wire.
      const target = conversationFixture({ name: 'Before' });
      loadList([target]);
      const toasts = TestBed.inject(ToastStore);

      store.beginRename(target);
      store.submitRename('After');
      store.cancelRename();

      // Busy tracks the request, not the dialog: exhaustMap is still occupied.
      expect(store.renameBusy()).toBe(true);

      expectPut().flush(PROBLEM_FIXTURES.validationError, {
        status: 400,
        statusText: 'Bad Request',
      });

      expect(list.entities()[0]?.name).toBe('Before');
      expect(store.renameError()).toBeNull();
      expect(toasts.assertiveToasts()).toHaveLength(1);
      expect(store.renameBusy()).toBe(false);
    });

    it('renames a conversation the list does not hold and still announces it', () => {
      // A deep link beyond the first page: nothing to patch, but the header needs the rename.
      const target = conversationFixture({ name: 'Unlisted' });
      loadList([conversationFixture({ name: 'Someone else' })]);

      store.beginRename(target);
      store.submitRename('Renamed');

      const fromServer = { ...target, name: 'Renamed' };
      expectPut().flush(fromServer);

      expect(list.entities().map((c) => c.name)).toEqual(['Someone else']);
      expect(updates).toEqual([fromServer]);
      expect(store.renameTarget()).toBeNull();
    });

    it('refuses to open the dialog for a row whose action is in flight', () => {
      const target = conversationFixture();
      loadList([target]);

      list.setRowPending(target.id, true);
      store.beginRename(target);

      expect(store.renameTarget()).toBeNull();
    });
  });

  describe('delete', () => {
    function deleteUrl(id: string): string {
      return `${TEST_API_BASE_URL}/api/conversations/${id}`;
    }

    it('closes the modal, removes the row optimistically and marks it pending', () => {
      const target = conversationFixture();
      loadList([target, conversationFixture()]);

      store.beginDelete(target);
      store.confirmDelete();

      expect(store.deleteTarget()).toBeNull();
      expect(list.entities().some((c) => c.id === target.id)).toBe(false);
      // Pending outlives the removed row; it keeps the header kebab disabled in flight.
      expect(list.isRowPending(target.id)).toBe(true);

      backend
        .expectOne({ method: 'DELETE', url: deleteUrl(target.id) })
        .flush(null, { status: 204, statusText: 'No Content' });
    });

    it('raises the success toast and announces the deletion on the 204', () => {
      const target = conversationFixture();
      loadList([target]);
      const toasts = TestBed.inject(ToastStore);

      store.beginDelete(target);
      store.confirmDelete();

      // Navigation waits for the server even though the row removal is optimistic.
      expect(deletions).toHaveLength(0);

      backend
        .expectOne({ method: 'DELETE', url: deleteUrl(target.id) })
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(toasts.politeToasts()).toHaveLength(1);
      expect(deletions).toEqual([target.id]);
      expect(list.isRowPending(target.id)).toBe(false);
    });

    it('restores the row at its position and raises an error toast on failure', () => {
      const [first, target, third] = [
        conversationFixture(),
        conversationFixture(),
        conversationFixture(),
      ];
      loadList([first, target, third]);
      const toasts = TestBed.inject(ToastStore);

      store.beginDelete(target);
      store.confirmDelete();
      backend
        .expectOne({ method: 'DELETE', url: deleteUrl(target.id) })
        .flush('', { status: 503, statusText: 'Service Unavailable' });

      expect(list.entities().map((c) => c.id)).toEqual([first.id, target.id, third.id]);
      expect(toasts.assertiveToasts()).toHaveLength(1);
      expect(deletions).toHaveLength(0);
      expect(list.isRowPending(target.id)).toBe(false);
    });

    it('lets deletes of different conversations overlap', () => {
      const [a, b] = [conversationFixture(), conversationFixture()];
      loadList([a, b]);

      store.beginDelete(a);
      store.confirmDelete();
      store.beginDelete(b);
      store.confirmDelete();

      const first = backend.expectOne({ method: 'DELETE', url: deleteUrl(a.id) });
      const second = backend.expectOne({ method: 'DELETE', url: deleteUrl(b.id) });
      first.flush(null, { status: 204, statusText: 'No Content' });
      second.flush(null, { status: 204, statusText: 'No Content' });

      expect(deletions).toEqual([a.id, b.id]);
    });

    it('deletes a conversation the list does not hold', () => {
      // A deep link: nothing to remove locally, but the deletion is announced so the open
      // screen can leave.
      const target = conversationFixture();
      loadList([conversationFixture()]);

      store.beginDelete(target);
      store.confirmDelete();

      backend
        .expectOne({ method: 'DELETE', url: deleteUrl(target.id) })
        .flush(null, { status: 204, statusText: 'No Content' });

      expect(list.entities()).toHaveLength(1);
      expect(deletions).toEqual([target.id]);
    });

    it('refuses to open the confirmation for a row whose action is in flight', () => {
      const target = conversationFixture();
      loadList([target]);

      list.setRowPending(target.id, true);
      store.beginDelete(target);

      expect(store.deleteTarget()).toBeNull();
    });

    it('drops the response and resets when the user signs out mid-delete', () => {
      const target = conversationFixture();
      loadList([target]);
      const toasts = TestBed.inject(ToastStore);

      store.beginDelete(target);
      store.confirmDelete();
      const request = backend.expectOne({ method: 'DELETE', url: deleteUrl(target.id) });

      signOut();

      expect(request.cancelled).toBe(true);
      expect(list.isRowPending(target.id)).toBe(false);
      expect(deletions).toHaveLength(0);
      expect(toasts.politeToasts()).toHaveLength(0);
      expect(toasts.assertiveToasts()).toHaveLength(0);
    });
  });

  describe('favorite', () => {
    function favoriteUrl(id: string): string {
      return `${TEST_API_BASE_URL}/api/conversations/${id}/favorite`;
    }

    function expectFavorite(id: string): TestRequest {
      return backend.expectOne({ method: 'PUT', url: favoriteUrl(id) });
    }

    function flush204(request: TestRequest): void {
      request.flush(null, { status: 204, statusText: 'No Content' });
    }

    it('flips the row optimistically and sends the state it is asking for', () => {
      const target = conversationFixture();
      loadList([target]);

      store.toggleFavorite(target);

      expect(list.entities()[0]?.isFavorite).toBe(true);
      expect(list.isRowPending(target.id)).toBe(true);
      // The server does not bump dateModified for a favourite, so neither does the patch.
      expect(list.entities()[0]?.dateModified).toBe(target.dateModified);

      const request = expectFavorite(target.id);
      // A SET, not a toggle: the body names the state, so a duplicate cannot invert it.
      expect(request.request.body).toEqual({ isFavorite: true });

      flush204(request);

      expect(list.isRowPending(target.id)).toBe(false);
      // The 204 carries no DTO, so the event is the target plus the accepted flag.
      expect(updates).toEqual([{ ...target, isFavorite: true }]);
    });

    it('unfavourites a favourited row', () => {
      const target = conversationFixture({ isFavorite: true });
      loadList([target]);

      store.toggleFavorite(target);

      expect(list.entities()[0]?.isFavorite).toBe(false);

      const request = expectFavorite(target.id);
      expect(request.request.body).toEqual({ isFavorite: false });
      flush204(request);

      expect(updates).toEqual([{ ...target, isFavorite: false }]);
    });

    it('rolls the flag back and raises a toast naming the trace id on failure', () => {
      const target = conversationFixture();
      loadList([target]);
      const toasts = TestBed.inject(ToastStore);

      store.toggleFavorite(target);
      expectFavorite(target.id).flush(PROBLEM_FIXTURES.providerNotConfigured, {
        status: 503,
        statusText: 'Service Unavailable',
      });

      expect(list.entities()[0]?.isFavorite).toBe(false);
      expect(toasts.assertiveToasts()).toHaveLength(1);
      expect(toasts.assertiveToasts()[0]?.traceLine).toContain(
        PROBLEM_FIXTURES.providerNotConfigured.traceId,
      );
      expect(updates).toHaveLength(0);
      expect(list.isRowPending(target.id)).toBe(false);
    });

    it('re-asserts the flag over a row a concurrent refresh clobbered mid-flight', () => {
      // The detail request's refreshRow can land the server's pre-PUT flag over the
      // optimistic patch while the header star is clickable.
      const target = conversationFixture();
      loadList([target]);

      store.toggleFavorite(target);
      const request = expectFavorite(target.id);

      list.refreshRow(target);
      expect(list.entities()[0]?.isFavorite).toBe(false);

      flush204(request);

      expect(list.entities()[0]?.isFavorite).toBe(true);
    });

    it('issues one request for a double toggle of the same row', () => {
      const target = conversationFixture();
      loadList([target]);

      store.toggleFavorite(target);
      store.toggleFavorite(target);

      // The pending id is set synchronously; otherwise expectOne would throw on an opposite PUT.
      const request = expectFavorite(target.id);
      expect(request.request.body).toEqual({ isFavorite: true });
      flush204(request);

      expect(list.entities()[0]?.isFavorite).toBe(true);
    });

    it('refuses a row whose own action is already in flight', () => {
      const target = conversationFixture();
      loadList([target]);

      list.setRowPending(target.id, true);
      store.toggleFavorite(target);

      backend.expectNone(favoriteUrl(target.id));
      expect(list.entities()[0]?.isFavorite).toBe(false);
    });

    it('lets favourites of different conversations overlap', () => {
      const [a, b] = [conversationFixture(), conversationFixture()];
      loadList([a, b]);

      store.toggleFavorite(a);
      store.toggleFavorite(b);

      const first = expectFavorite(a.id);
      const second = expectFavorite(b.id);
      flush204(first);
      flush204(second);

      expect(updates).toEqual([
        { ...a, isFavorite: true },
        { ...b, isFavorite: true },
      ]);
    });

    it('favourites a conversation the list does not hold and still announces it', () => {
      // A deep link: nothing to patch locally, but the header still needs the confirmed flag.
      const target = conversationFixture();
      loadList([conversationFixture()]);

      store.toggleFavorite(target);

      expect(list.entities()).toHaveLength(1);
      expect(list.entities().some((c) => c.isFavorite)).toBe(false);

      flush204(expectFavorite(target.id));

      expect(updates).toEqual([{ ...target, isFavorite: true }]);
    });

    it('drops the response and clears pending when the user signs out mid-toggle', () => {
      const target = conversationFixture();
      loadList([target]);
      const toasts = TestBed.inject(ToastStore);

      store.toggleFavorite(target);
      const request = expectFavorite(target.id);

      signOut();

      expect(request.cancelled).toBe(true);
      // finalize runs on cancellation too, or the id would stay pending for the next user.
      expect(list.isRowPending(target.id)).toBe(false);
      expect(updates).toHaveLength(0);
      expect(toasts.assertiveToasts()).toHaveLength(0);
    });
  });

  describe('move to project', () => {
    const PROJECT_A = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';
    const PROJECT_B = 'bbbbbbbb-cccc-4ddd-8eee-ffffffffffff';

    it('sends the target project with the unchanged name echoed', () => {
      const target = conversationFixture({ name: 'Cutover comms', projectId: null });
      loadList([target]);

      store.moveToProject(target, PROJECT_A);

      const request = expectPut();
      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({
        id: target.id,
        name: 'Cutover comms',
        projectId: PROJECT_A,
      });
      request.flush({ ...target, projectId: PROJECT_A });
    });

    it('sends an explicit null when the conversation is removed from its project', () => {
      // An undefined would echo the current project, making "remove from project" a no-op.
      const target = conversationFixture({ projectId: PROJECT_A });
      loadList([target]);

      store.moveToProject(target, null);

      const request = expectPut();
      expect(request.request.body.projectId).toBeNull();
      expect('projectId' in request.request.body).toBe(true);
      request.flush({ ...target, projectId: null });

      expect(list.entityMap()[target.id]?.projectId).toBeNull();
    });

    it('leaves A and appears under B in one round trip', () => {
      const target = conversationFixture({ projectId: PROJECT_A });
      loadList([target]);

      store.moveToProject(target, PROJECT_B);

      expect(list.entityMap()[target.id]?.projectId).toBe(PROJECT_B);
      expect(list.isRowPending(target.id)).toBe(true);

      const request = expectPut();
      expect(request.request.body.projectId).toBe(PROJECT_B);
      request.flush({ ...target, projectId: PROJECT_B, dateModified: '2026-08-15T10:00:00+00:00' });

      backend.expectNone(PUT_URL);
      expect(list.entityMap()[target.id]?.projectId).toBe(PROJECT_B);
      // Adopted from the server's DTO: a move bumps dateModified.
      expect(list.entityMap()[target.id]?.dateModified).toBe('2026-08-15T10:00:00+00:00');
      expect(list.isRowPending(target.id)).toBe(false);
      expect(updates).toEqual([expect.objectContaining({ id: target.id, projectId: PROJECT_B })]);
    });

    it('rolls the row back to its previous project and toasts on failure', () => {
      const target = conversationFixture({ projectId: PROJECT_A });
      loadList([target]);
      const toasts = TestBed.inject(ToastStore);

      store.moveToProject(target, PROJECT_B);
      expect(list.entityMap()[target.id]?.projectId).toBe(PROJECT_B);

      expectPut().flush(PROBLEM_FIXTURES.resourceNotFound, {
        status: 404,
        statusText: 'Not Found',
      });

      expect(list.entityMap()[target.id]?.projectId).toBe(PROJECT_A);
      expect(list.isRowPending(target.id)).toBe(false);
      expect(toasts.assertiveToasts()).toHaveLength(1);
      expect(updates).toHaveLength(0);
    });

    it('refuses a second move while the row’s own action is in flight', () => {
      // Without the guard, two racing moves would settle the project by arrival order.
      const target = conversationFixture({ projectId: null });
      loadList([target]);

      store.moveToProject(target, PROJECT_A);
      store.moveToProject(target, PROJECT_B);

      expectPut().flush({ ...target, projectId: PROJECT_A });
      expect(list.entityMap()[target.id]?.projectId).toBe(PROJECT_A);
    });

    it('is a no-op when the conversation is already in that project', () => {
      const target = conversationFixture({ projectId: PROJECT_A });
      loadList([target]);

      store.moveToProject(target, PROJECT_A);

      backend.expectNone(PUT_URL);
      expect(list.isRowPending(target.id)).toBe(false);
    });

    it('moves a row the sidebar does not hold, with nothing to patch', () => {
      // A deep link past the first 50: the event carries the change to the open conversation.
      const target = conversationFixture({ projectId: null });
      loadList([]);

      store.moveToProject(target, PROJECT_A);

      expectPut().flush({ ...target, projectId: PROJECT_A });
      expect(updates).toEqual([expect.objectContaining({ id: target.id, projectId: PROJECT_A })]);
    });

    it('drops the response and clears pending when the user signs out mid-move', () => {
      const target = conversationFixture({ projectId: null });
      loadList([target]);

      store.moveToProject(target, PROJECT_A);
      const request = expectPut();

      signOut();

      expect(request.cancelled).toBe(true);
      expect(list.isRowPending(target.id)).toBe(false);
      expect(updates).toHaveLength(0);
    });
  });
});
