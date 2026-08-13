import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import {
  StreamingResponseHandle,
  assistantEvent,
  frame,
  fullTurnEvents,
  problemResponse,
  streamingResponse,
} from '@testing/stream-frames';
import { TurnSelection, TurnSettingsStore } from '@core/chat/turn-settings-store';
import { TokenService } from '@core/auth/token-service';
import { STREAM_BATCH_WINDOW_MS } from '@core/stream/conversation-stream-client';
import { STREAM_FETCH } from '@core/stream/stream-fetch.token';
import { provideChatMarkdown } from '../markdown/markdown-providers';
import { TurnStore } from '../turn-store';
import { Transcript } from './transcript';

const CONVERSATION_ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';
const SELECTION: TurnSelection = { modelId: 'model-1', mcpServerIds: [] };

describe('Transcript', () => {
  let fixture: ComponentFixture<Transcript>;
  let host: HTMLElement;
  let store: InstanceType<typeof TurnStore>;
  let backend: HttpTestingController;
  let fetchMock: ReturnType<typeof vi.fn>;
  let applyConversationSettings: ReturnType<typeof vi.fn>;
  let writeText: ReturnType<typeof vi.fn>;

  let clipboardDescriptor: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.useFakeTimers();
    fetchMock = vi.fn();
    applyConversationSettings = vi.fn();
    writeText = vi.fn().mockResolvedValue(undefined);
    clipboardDescriptor = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    });

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: fetchMock },
        { provide: TokenService, useValue: { getToken: vi.fn().mockResolvedValue('token-1') } },
        { provide: Router, useValue: { navigateByUrl: vi.fn().mockResolvedValue(true) } },
        {
          provide: TurnSettingsStore,
          useValue: { streamSelection: () => SELECTION, applyConversationSettings },
        },
        TurnStore,
        // US-601: the transcript renders markdown, so its specs need the same
        // sanitized pipeline the lazy chat route provides in the application.
        provideChatMarkdown(),
      ],
    });

    store = TestBed.inject(TurnStore);
    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Transcript);
    document.body.append(fixture.nativeElement as HTMLElement);
    host = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => {
    host.remove();
    if (clipboardDescriptor !== undefined) {
      Object.defineProperty(navigator, 'clipboard', clipboardDescriptor);
    } else {
      Reflect.deleteProperty(navigator, 'clipboard');
    }
    vi.useRealTimers();
  });

  /**
   * Advances fake time, then waits the zoneless scheduler out. `whenStable`
   * is started *before* the second advance because the scheduler notifies
   * through rAF/setTimeout, which the fake clock holds until advanced.
   */
  async function settle(ms = 0): Promise<void> {
    await vi.advanceTimersByTimeAsync(ms);
    const stable = fixture.whenStable();
    await vi.advanceTimersByTimeAsync(20);
    await stable;
  }

  async function startTurn(handle = streamingResponse()): Promise<StreamingResponseHandle> {
    fetchMock.mockImplementation((_input: unknown, init?: RequestInit) => {
      handle.abortOn(init?.signal);
      return Promise.resolve(handle.response);
    });
    store.bindRoute(CONVERSATION_ID);
    // Opening a conversation reads its stored messages (US-410); an empty
    // transcript is what these specs are about, and the read has to be
    // answered or the region stays aria-busy for the rest of the test.
    backend
      .expectOne(`${TEST_API_BASE_URL}/api/conversations/${CONVERSATION_ID}/messages`)
      .flush({ id: CONVERSATION_ID, name: 'Conversation', messages: [] });
    store.send('What is the weather?');
    await settle();
    return handle;
  }

  async function streamFullTurn(): Promise<void> {
    const handle = await startTurn();
    handle.enqueue(fullTurnEvents().map(frame).join(''));
    handle.close();
    await settle(STREAM_BATCH_WINDOW_MS);
  }

  function cardNames(): string[] {
    return [...host.querySelectorAll('.activity-card__name')].map(
      (name) => name.textContent?.trim() ?? '',
    );
  }

  describe('the activity timeline (US-501)', () => {
    it('renders cards and text in exact arrival order, nested work included', async () => {
      await streamFullTurn();

      // The recorded turn: mcp-1, then its child agent-1, then fn-1, then text.
      expect(cardNames()).toEqual(['Andes Test MCP', 'Forecast Agent', 'Search Documents']);

      const content = host.querySelector('.assistant-turn__content');
      const sequence = [...(content?.children ?? [])].map((child) => child.tagName.toLowerCase());
      expect(sequence).toEqual([
        'app-activity-card',
        'app-activity-card',
        'app-activity-card',
        'markdown',
      ]);
      expect(host.querySelector('.assistant-turn__md')?.textContent?.trim()).toBe('Hello, world.');
    });

    it('badges the kind separately from the label, never composed', async () => {
      await streamFullTurn();

      const badges = [...host.querySelectorAll('app-kind-badge')].map(
        (badge) => badge.textContent?.trim() ?? '',
      );
      expect(badges).toEqual(['MCP tool', 'Agent', 'Function']);
      // No pre-composed "Calling …" strings anywhere on the cards.
      expect(host.textContent).not.toContain('Calling');
    });

    it('badges an Unknown-kind activity as Reasoning', async () => {
      const handle = await startTurn();
      handle.enqueue(
        frame(
          assistantEvent('ActivityStarted', {
            scopeId: 'r-1',
            displayName: 'Thinking it through',
            source: undefined,
            toolKind: 'Unknown',
          }),
        ),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(host.querySelector('app-kind-badge')?.textContent?.trim()).toBe('Reasoning');
    });

    it('renders the source verbatim and the sub-statuses as an indented list', async () => {
      await streamFullTurn();

      const sources = [...host.querySelectorAll('.activity-card__source')].map(
        (source) => source.textContent?.trim() ?? '',
      );
      expect(sources).toEqual(['Weather', 'agent:forecast']);
      expect(host.querySelector('.activity-card__substatus')?.textContent?.trim()).toBe(
        'Fetching forecast…',
      );
    });

    it('shows the running ring, then completion and failure with mono durations', async () => {
      const handle = await startTurn();
      handle.enqueue(frame(assistantEvent('ActivityStarted', { scopeId: 'mcp-1' })));
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(host.querySelector('.activity-card__ring')).not.toBeNull();
      expect(host.querySelector('.activity-card__duration')).toBeNull();

      handle.enqueue(frame(assistantEvent('ActivityCompleted', { scopeId: 'mcp-1' })));
      handle.enqueue(
        frame(assistantEvent('ActivityStarted', { scopeId: 'fn-1', toolKind: 'Function' })),
      );
      handle.enqueue(
        frame(assistantEvent('ActivityFailed', { scopeId: 'fn-1', toolKind: 'Function' })),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(host.querySelector('.activity-card__ring')).toBeNull();
      expect(host.querySelector('.activity-card__state--ok')).not.toBeNull();
      expect(host.querySelector('.activity-card__state--warn')).not.toBeNull();
      const durations = [...host.querySelectorAll('.activity-card__duration')].map(
        (duration) => duration.textContent?.trim() ?? '',
      );
      expect(durations).toEqual(['1.4s', '0.6s']);
    });
  });

  describe('watching the answer arrive (US-406)', () => {
    it('shows the thinking ridgeline until renderable content arrives', async () => {
      const handle = await startTurn();

      expect(host.querySelector('app-ridgeline')).not.toBeNull();
      expect(host.getAttribute('aria-busy')).toBe('true');
      // The optimistic user bubble is already on screen.
      expect(host.querySelector('.transcript__bubble')?.textContent).toBe('What is the weather?');

      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Hello' })));
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(host.querySelector('app-ridgeline')).toBeNull();
      // Mid-stream the growing block is a head/tail pair (US-602), and text this
      // short has no block boundary yet — so it is the tail that carries it.
      expect(host.querySelector('.assistant-turn__content')?.textContent).toContain('Hello');
      expect(host.querySelector('.assistant-turn__md--caret')).not.toBeNull();
    });

    it('drops aria-busy and the caret once the turn settles', async () => {
      await streamFullTurn();

      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.querySelector('.assistant-turn__md--caret')).toBeNull();
    });

    it('renders a cut-off turn as the warning card above the partial answer', async () => {
      const handle = await startTurn();
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Partial ans' })));
      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);

      const notice = host.querySelector('app-turn-notice-card .notice');
      expect(notice?.classList.contains('notice--warn')).toBe(true);
      expect(notice?.textContent).toContain('This response was cut off');
      expect(notice?.textContent).toContain('the answer below may be incomplete');
      expect(notice?.querySelector('.notice__retry')).not.toBeNull();
      expect(host.querySelector('.assistant-turn__md')?.textContent?.trim()).toBe('Partial ans');
      // The persistent live region carries the announcement — the card is not
      // a live region, because it appears together with its content.
      expect(notice?.getAttribute('role')).toBeNull();
      expect(host.querySelector('[role="status"]')?.textContent).toContain('cut off');
    });
  });

  describe('pre-stream failure notices', () => {
    async function failWith(problem: object, status: number): Promise<void> {
      fetchMock.mockResolvedValue(problemResponse(problem, status).response);
      store.bindRoute(CONVERSATION_ID);
      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/${CONVERSATION_ID}/messages`)
        .flush({ id: CONVERSATION_ID, name: 'Conversation', messages: [] });
      store.send('What is the weather?');
      await settle();
    }

    it('renders the 409 as the busy row with a Retry (US-408)', async () => {
      await failWith(PROBLEM_FIXTURES.conversationBusy, 409);

      const notice = host.querySelector('app-turn-notice-card .notice');
      expect(notice?.classList.contains('notice--warn')).toBe(true);
      expect(notice?.classList.contains('notice--row')).toBe(true);
      expect(notice?.textContent).toContain('Another response is still running in this');
      expect(notice?.querySelector('.notice__retry')).not.toBeNull();
    });

    it('re-sends on Retry and hands the control back when it fails again (US-408)', async () => {
      await failWith(PROBLEM_FIXTURES.conversationBusy, 409);

      host.querySelector<HTMLButtonElement>('.notice__retry')?.click();
      await settle();

      // The other tab is still holding the conversation, so the same 409 comes
      // back — and the replacement Retry takes the focus its destroyed
      // predecessor left on <body>, so the loop stays operable by keyboard.
      expect(fetchMock).toHaveBeenCalledTimes(2);
      expect(document.activeElement).toBe(host.querySelector('.notice__retry'));
    });

    it('renders the 403 as the consent card with no action (US-412)', async () => {
      await failWith(PROBLEM_FIXTURES.mcpAuthorizationRequired, 403);

      const notice = host.querySelector('app-turn-notice-card .notice');
      expect(notice?.classList.contains('notice--warn')).toBe(false);
      expect(notice?.textContent).toContain('Weather requires authorization');
      expect(notice?.textContent).toContain('contact your administrator');
      // Retry is withheld by `canRetry`, not by the card: consent cannot be
      // supplied by repeating the call.
      expect(notice?.querySelector('button')).toBeNull();
      // What a screen reader is told has to agree with what the card says.
      expect(host.querySelector('[role="status"]')?.textContent).toContain('administrator');
    });
  });

  describe('stopping a turn (US-407)', () => {
    async function stopMidStream(): Promise<void> {
      const handle = await startTurn();
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Partial ans' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      store.stop();
      await settle(STREAM_BATCH_WINDOW_MS);
    }

    it('renders the detached dashed card and removes the user bubble', async () => {
      await stopMidStream();

      const stopped = host.querySelector('.stopped');
      expect(stopped).not.toBeNull();
      expect(stopped?.textContent).toContain('Stopped — not saved to this conversation');
      expect(stopped?.textContent).toContain('Partial ans');
      // The prompt bubble disappears with the turn: nothing was transcribed.
      expect(host.querySelector('.transcript__bubble')).toBeNull();
      expect(host.querySelector('app-assistant-turn')).toBeNull();
      // The persistent live region announces the stop; the card is not a live region.
      expect(host.querySelector('[role="status"]')?.textContent).toContain('not saved');
    });

    it('copies the partial text and confirms for two seconds', async () => {
      await stopMidStream();

      const copy = [...host.querySelectorAll('.stopped__action')].find((button) =>
        button.textContent?.includes('Copy'),
      ) as HTMLButtonElement;
      copy.click();
      await settle();

      expect(writeText).toHaveBeenCalledExactlyOnceWith('Partial ans');
      expect(copy.textContent).toContain('Copied');
      await settle(2000);
      expect(copy.textContent).toContain('Copy');
    });

    it('restores the prompt and settings through Retry', async () => {
      await stopMidStream();

      const retry = [...host.querySelectorAll('.stopped__action')].find((button) =>
        button.textContent?.includes('Retry'),
      ) as HTMLButtonElement;
      retry.click();
      await settle();

      expect(applyConversationSettings).toHaveBeenCalledExactlyOnceWith({
        modelId: SELECTION.modelId,
        mcpServerIds: SELECTION.mcpServerIds,
      });
      expect(store.composerSeed()?.text).toBe('What is the weather?');
      expect(host.querySelector('.stopped')).toBeNull();
    });
  });
});
