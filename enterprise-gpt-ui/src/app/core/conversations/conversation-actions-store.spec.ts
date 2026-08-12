import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { Subscription } from 'rxjs';
import { ConversationDto } from '@domain/api/conversation';
import { conversationEvents } from '@core/events/conversation-events';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConversationActionsStore } from './conversation-actions-store';
import { ConversationListStore } from './conversation-list-store';

const PUT_URL = `${TEST_API_BASE_URL}/api/conversations`;
const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

describe('ConversationActionsStore', () => {
  let store: InstanceType<typeof ConversationActionsStore>;
  let list: InstanceType<typeof ConversationListStore>;
  let backend: HttpTestingController;

  /** The `updated` / `deleted` events observed during a test, in dispatch order. */
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
    // Explicitly unsubscribed in afterEach: `Events` is platform-scoped, so it
    // outlives `resetTestingModule` and a leaked subscription from one test would
    // keep pushing into every later test's array.
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

  /** Puts rows into the sidebar list, since the flows patch it optimistically. */
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
      // The load-bearing criterion: this PUT replaces the whole representation, so a
      // body without the project id would silently unlink the conversation.
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

      // The server's dateModified, not a local guess.
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
      // finalize runs on cancellation too — without it the id would stay pending
      // for the next user.
      expect(list.isRowPending(target.id)).toBe(false);
      expect(toasts.assertiveToasts()).toHaveLength(0);
    });

    it('degrades a validation rejection to a toast when the dialog closed mid-flight', () => {
      // A second Escape is uncancelable in Chromium, so a busy dialog can be
      // force-closed with the PUT still on the wire. A validation problem landing
      // then has no field to render under — it must surface as a toast, not vanish.
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
      // The deep-link case: /chat/{id} beyond the first page. The list has nothing
      // to patch, but the header still needs the confirmed rename.
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
      // Pending outlives the vanished row: it is what keeps the header kebab
      // disabled while the request is on the wire.
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

      // Nothing announced before the server confirms — navigation must not be
      // optimistic even though the row removal is.
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
      // mergeMap, not exhaustMap: deleting B while A is still on the wire is legal
      // and must issue both requests.
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
      // The deep-link case: nothing to remove locally, but the DELETE still goes
      // out and the completion is still announced so the open screen can leave.
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
});
