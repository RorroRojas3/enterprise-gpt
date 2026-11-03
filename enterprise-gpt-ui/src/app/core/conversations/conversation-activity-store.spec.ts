import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConversationDto } from '@core/api/conversation.model';
import { conversationEvents } from '@core/events/conversation-events';
import { sessionEvents } from '@core/events/session-events';
import { turnEvents } from '@core/events/turn-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationDetailFixture, conversationFixture } from '@testing/conversations';
import { ConversationActivityStore } from './conversation-activity-store';
import { ConversationListStore } from './conversation-list-store';

describe('ConversationActivityStore', () => {
  const held = conversationFixture({ name: 'Helios release' });

  let store: InstanceType<typeof ConversationActivityStore>;
  let toasts: InstanceType<typeof ToastStore>;
  let backend: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;
  let stop: ReturnType<typeof vi.fn<(conversationId: string) => void>>;
  const running = signal<ReadonlySet<string>>(new Set());
  const runningCount = signal(0);
  const viewing = signal<string | null>(null);

  function setup(chat?: { maxConcurrentTurns?: number }): void {
    running.set(new Set());
    runningCount.set(0);
    viewing.set(null);
    navigate = vi.fn().mockResolvedValue(true);
    stop = vi.fn<(conversationId: string) => void>();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(chat === undefined ? {} : { chat }),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate } },
      ],
    });

    store = TestBed.inject(ConversationActivityStore);
    toasts = TestBed.inject(ToastStore);
    backend = TestBed.inject(HttpTestingController);
    TestBed.inject(ConversationListStore).prependNewest(held);
    store.connect({ running, runningCount, viewing, stop });
  }

  beforeEach(() => setup());
  afterEach(() => backend.verify());

  function dispatch(event: Parameters<Dispatcher['dispatch']>[0]): void {
    TestBed.inject(Dispatcher).dispatch(event);
  }

  function complete(conversationId: string, wasFirstTurn = false): void {
    dispatch(turnEvents.completed({ conversationId, wasFirstTurn }));
  }

  function answerDetail(id: string, body: object, failed = false): void {
    backend
      .expectOne(`${TEST_API_BASE_URL}/api/conversations/${id}`)
      .flush(body, failed ? { status: 500, statusText: 'Internal Server Error' } : {});
  }

  it('reports a conversation as running for as long as its registry says so', () => {
    running.set(new Set([held.id]));
    expect(store.activityOf(held.id)).toBe('running');

    running.set(new Set());
    expect(store.activityOf(held.id)).toBeNull();
  });

  it('marks an answer that finished out of sight, and offers to open it', () => {
    viewing.set('somewhere-else');

    complete(held.id);

    expect(store.activityOf(held.id)).toBe('answer');
    expect(store.summarySuffix()).toBe(' — 1 new answer');
    const [toast] = toasts.toasts();
    expect(toast).toMatchObject({ tone: 'info', title: 'Answer ready', detail: 'Helios release' });
    expect(toast?.actionLabel).toBe('Open');

    toasts.runAction(toast!.id);
    expect(navigate).toHaveBeenCalledExactlyOnceWith(['/chat', held.id]);
  });

  it('keeps one toast per conversation, cleared once the conversation is seen', () => {
    complete(held.id);
    complete(held.id);

    expect(toasts.toasts()).toHaveLength(1);

    store.markSeen(held.id);
    expect(toasts.toasts()).toHaveLength(0);
  });

  it('marks nothing for the conversation on screen', () => {
    viewing.set(held.id);

    complete(held.id);
    dispatch(turnEvents.interrupted({ conversationId: held.id }));

    expect(store.activityOf(held.id)).toBeNull();
    expect(toasts.toasts()).toHaveLength(0);
  });

  it('looks up the name of a conversation the sidebar does not hold', () => {
    const elsewhere = conversationFixture({ name: 'Opened from the library' });

    complete(elsewhere.id);
    expect(toasts.toasts()).toHaveLength(0);
    answerDetail(elsewhere.id, conversationDetailFixture({ ...elsewhere }));

    expect(toasts.toasts().map((toast) => toast.detail)).toEqual(['Opened from the library']);
  });

  it('still announces an answer whose name cannot be looked up', () => {
    const elsewhere = conversationFixture();

    complete(elsewhere.id);
    answerDetail(elsewhere.id, { title: 'Boom' }, true);

    expect(toasts.toasts().map((toast) => toast.detail)).toEqual([null]);
  });

  describe('a first turn’s generated name', () => {
    const generated = conversationDetailFixture({ ...held, name: 'Helios 2.4 release status' });

    it('reaches the sidebar and every listener, as list fields only', () => {
      viewing.set(held.id);
      const updated: ConversationDto[] = [];
      TestBed.inject(Events)
        .on(conversationEvents.updated)
        .subscribe(({ payload }) => updated.push(payload));

      complete(held.id, true);
      answerDetail(held.id, generated);

      expect(TestBed.inject(ConversationListStore).entityMap()[held.id]?.name).toBe(
        'Helios 2.4 release status',
      );
      expect(updated.map((conversation) => conversation.name)).toEqual([
        'Helios 2.4 release status',
      ]);
      expect(updated[0]).not.toHaveProperty('mcpServerIds');
      expect(toasts.toasts()).toHaveLength(0);
    });

    it('names the toast of an answer that finished out of sight', () => {
      complete(held.id, true);
      expect(toasts.toasts()).toHaveLength(0);

      answerDetail(held.id, generated);

      expect(toasts.toasts().map((toast) => toast.detail)).toEqual(['Helios 2.4 release status']);
    });

    it('names two conversations whose first turns finish together', () => {
      const other = conversationFixture();
      TestBed.inject(ConversationListStore).prependNewest(other);

      complete(held.id, true);
      complete(other.id, true);
      answerDetail(held.id, generated);
      answerDetail(other.id, conversationDetailFixture({ ...other, name: 'Two' }));

      expect(toasts.toasts().map((toast) => toast.detail)).toEqual([
        'Helios 2.4 release status',
        'Two',
      ]);
    });

    it('still announces the answer when the name cannot be read', () => {
      complete(held.id, true);
      answerDetail(held.id, { title: 'Boom' }, true);

      expect(toasts.toasts().map((toast) => toast.detail)).toEqual([null]);
    });

    it('skips the announcement once the answer has been seen', () => {
      complete(held.id, true);
      store.markSeen(held.id);
      answerDetail(held.id, generated);

      expect(toasts.toasts()).toHaveLength(0);
    });
  });

  it('flags an interrupted answer without a toast, which would never dismiss itself', () => {
    dispatch(turnEvents.interrupted({ conversationId: held.id }));

    expect(store.activityOf(held.id)).toBe('interrupted');
    expect(toasts.toasts()).toHaveLength(0);
  });

  it('clears a mark once the conversation is opened, or deleted', () => {
    const other = conversationFixture();
    dispatch(turnEvents.interrupted({ conversationId: held.id }));
    dispatch(turnEvents.interrupted({ conversationId: other.id }));
    expect(store.summarySuffix()).toBe(' — 2 interrupted');

    store.markSeen(held.id);
    dispatch(conversationEvents.deleted(other.id));

    expect(store.summary()).toBeNull();
  });

  it('shows a running turn over an older unseen ending', () => {
    dispatch(turnEvents.interrupted({ conversationId: held.id }));
    running.set(new Set([held.id]));
    runningCount.set(1);

    expect(store.activityOf(held.id)).toBe('running');
    expect(store.summary()).toBe('running');
    expect(store.summarySuffix()).toBe(' — 1 generating, 1 interrupted');
  });

  it('forgets every mark on sign-out', () => {
    dispatch(turnEvents.interrupted({ conversationId: held.id }));

    dispatch(sessionEvents.signedOut());

    expect(store.summary()).toBeNull();
  });

  it('forwards a stop to the registry', () => {
    store.stop(held.id);

    expect(stop).toHaveBeenCalledExactlyOnceWith(held.id);
  });

  it('reaches the cap the deployment set, counting a first prompt with no id yet', () => {
    setup({ maxConcurrentTurns: 2 });

    runningCount.set(1);
    expect(store.atCapacity()).toBe(false);

    runningCount.set(2);
    expect(store.atCapacity()).toBe(true);
  });

  it('defaults the cap to four when the deployment sets none', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig({ chat: undefined }),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    backend = TestBed.inject(HttpTestingController);

    expect(TestBed.inject(ConversationActivityStore).maxConcurrentTurns).toBe(4);
  });
});
