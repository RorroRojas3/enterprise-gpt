import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { getByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AccessTokenClient } from '@core/auth/access-token-client';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { chatMatcher } from '@core/navigation/chat-matcher';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { conversationDetailFixture, conversationFixture } from '@testing/conversations';
import {
  observerFor,
  resetIntersectionObservers,
  setIntersecting,
} from '@testing/intersection-observer';
import { resetResizeObservers, resizeElement } from '@testing/resize-observer';
import {
  StreamingResponseHandle,
  assistantEvent,
  frame,
  fullTurnEvents,
  streamingResponse,
} from '@testing/stream-frames';
import { Chat } from './chat';
import { STREAM_BATCH_WINDOW_MS } from './conversation-stream-client';
import { STREAM_FETCH } from './stream-fetch-token';
import { TurnStore } from './turn-store';

/** jsdom has no layout, so these assert the scroll writes the directive makes, not positions. */
describe('transcript pinning', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let streamFetch: ReturnType<typeof vi.fn>;
  let scrollWrites: number[];

  const conversation = conversationFixture({ name: 'Helios 2.4 release status' });

  /** Grows per batch, so a pin taken before layout records the old height and fails. */
  let contentHeight = 1000;

  beforeEach(async () => {
    vi.useFakeTimers();
    resetIntersectionObservers();
    resetResizeObservers();
    scrollWrites = [];
    contentHeight = 1000;
    streamFetch = vi.fn(() => new Promise<Response>(() => {}));

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: streamFetch },
        {
          provide: AccessTokenClient,
          useValue: { getToken: vi.fn().mockResolvedValue('token-1') },
        },
        provideRouter([{ matcher: chatMatcher, component: Chat }], withComponentInputBinding()),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function body(): HTMLElement {
    return element().querySelector('.chat__body') as HTMLElement;
  }

  /** The scroll box's last child; its first is the content wrapper the directive measures. */
  function sentinel(): HTMLElement {
    return body().lastElementChild as HTMLElement;
  }

  function jump(): HTMLElement | null {
    return element().querySelector('.chat__jump');
  }

  /** The store is provided by `Chat` itself, so it comes from the route's injector. */
  function turnStore(): InstanceType<typeof TurnStore> {
    return harness.routeDebugElement!.injector.get(TurnStore);
  }

  /** Records scroll writes; jsdom would otherwise report a constant zero. */
  function instrumentScrolling(): void {
    let scrollTop = 0;
    Object.defineProperty(body(), 'scrollHeight', {
      get: () => contentHeight,
      configurable: true,
    });
    Object.defineProperty(body(), 'scrollTop', {
      get: () => scrollTop,
      set: (value: number) => {
        scrollTop = value;
        scrollWrites.push(value);
      },
      configurable: true,
    });
  }

  /** Separate from the batch: markdown lays out after change detection, as in the browser. */
  function layOutTallerContent(): void {
    contentHeight += 250;
    resizeElement(body().firstElementChild as HTMLElement);
  }

  async function settle(ms = 0): Promise<void> {
    await vi.advanceTimersByTimeAsync(ms);
    const stable = harness.fixture.whenStable();
    await vi.advanceTimersByTimeAsync(20);
    await stable;
  }

  /** A turn needs a model, so the catalog is loaded rather than the store stubbed. */
  async function loadModels(): Promise<void> {
    const models = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(`${TEST_API_BASE_URL}/api/models`).flush([modelFixture({ isDefault: true })]);
    await models;
    await settle();
  }

  /** Opens the conversation and starts a turn that stays in flight. */
  async function openStreamingConversation(): Promise<StreamingResponseHandle> {
    const handle = streamingResponse();
    streamFetch.mockImplementation((_input: unknown, init?: RequestInit) => {
      handle.abortOn(init?.signal);
      return Promise.resolve(handle.response);
    });

    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}`)
      .flush(conversationDetailFixture({ ...conversation }));
    // Nothing said yet, so the empty state shows until the turn starts.
    backend
      .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}/messages`)
      .flush({ id: conversation.id, name: conversation.name, messages: [] });
    await settle();

    await loadModels();

    instrumentScrolling();
    turnStore().send('What is the weather?');
    await settle();

    return handle;
  }

  async function push(handle: StreamingResponseHandle, text: string): Promise<void> {
    handle.enqueue(frame(assistantEvent('TextDelta', { text })));
    await settle(STREAM_BATCH_WINDOW_MS);
    layOutTallerContent();
  }

  it('observes a zero-height sentinel against the scroll container, with the 80px margin', async () => {
    await openStreamingConversation();

    const observer = observerFor(sentinel());
    expect(observer).toBeDefined();
    expect(observer?.root).toBe(body());
    expect(observer?.rootMargin).toBe('0px 0px 80px 0px');
  });

  it('follows the newest content while the reader is at the bottom', async () => {
    const handle = await openStreamingConversation();
    scrollWrites = [];

    await push(handle, 'The forecast is ');

    expect(scrollWrites.at(-1)).toBe(contentHeight);
    expect(jump()).toBeNull();
  });

  it('holds position and offers the control once the reader scrolls up', async () => {
    const handle = await openStreamingConversation();
    setIntersecting(sentinel(), false);
    await settle();
    scrollWrites = [];

    await push(handle, 'a great deal more text arriving');

    expect(scrollWrites).toHaveLength(0);
    const control = jump();
    expect(control).not.toBeNull();
    expect(control?.classList.contains('chat__jump--pulsing')).toBe(true);
    expect(control?.querySelector('button')?.getAttribute('aria-label')).toBe(
      'Jump to latest message',
    );
  });

  it('returns to the bottom when the control is pressed, and resumes following', async () => {
    const handle = await openStreamingConversation();
    setIntersecting(sentinel(), false);
    await settle();
    scrollWrites = [];

    getByRole(element(), 'button', { name: 'Jump to latest message' }).click();
    await settle();

    expect(scrollWrites.at(-1)).toBe(contentHeight);
    expect(jump()).toBeNull();

    // Pinning is live again, without waiting for the observer to say so.
    scrollWrites = [];
    await push(handle, ' and more');
    expect(scrollWrites.at(-1)).toBe(contentHeight);
  });

  it('hands focus to the transcript rather than dropping it when the control goes', async () => {
    await openStreamingConversation();
    setIntersecting(sentinel(), false);
    await settle();

    const button = getByRole(element(), 'button', { name: 'Jump to latest message' });
    button.focus();
    expect(document.activeElement).toBe(button);

    button.click();
    await settle();

    expect(jump()).toBeNull();
    expect(document.activeElement).toBe(body());
  });

  it('does not jump when a turn finishes while the reader is scrolled up', async () => {
    const handle = await openStreamingConversation();
    setIntersecting(sentinel(), false);
    await settle();
    scrollWrites = [];

    handle.enqueue(fullTurnEvents().map(frame).join(''));
    handle.close();
    await settle(STREAM_BATCH_WINDOW_MS);
    // Settling is the turn's largest height change, and still must not move the view.
    layOutTallerContent();

    expect(scrollWrites).toHaveLength(0);
    // The way back stays on screen; only the streaming pulse stops.
    const control = jump();
    expect(control).not.toBeNull();
    expect(control?.classList.contains('chat__jump--pulsing')).toBe(false);
  });

  it('keeps watching the same sentinel when the body swaps from empty state to transcript', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}`)
      .flush(conversationDetailFixture({ ...conversation }));
    // Nothing said yet, so the body starts on the empty state this test swaps away from.
    backend
      .expectOne(`${TEST_API_BASE_URL}/api/conversations/${conversation.id}/messages`)
      .flush({ id: conversation.id, name: conversation.name, messages: [] });
    await settle();

    const before = sentinel();
    expect(element().querySelector('app-chat-empty-state')).not.toBeNull();

    await loadModels();

    instrumentScrolling();
    turnStore().send('What is the weather?');
    await settle();

    expect(element().querySelector('app-transcript')).not.toBeNull();
    expect(sentinel()).toBe(before);
    expect(observerFor(before)).toBeDefined();
  });

  it('shows no control on a screen the reader has never scrolled', async () => {
    await harness.navigateByUrl('/chat', Chat);
    await settle();

    expect(jump()).toBeNull();
  });
});
