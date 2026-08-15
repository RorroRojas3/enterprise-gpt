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
import { CHAT_ROLE } from '@domain/api/conversation';
import { TurnSelection, TurnSettingsStore } from '@core/chat/turn-settings-store';
import { TokenService } from '@core/auth/token-service';
import { STREAM_BATCH_WINDOW_MS } from '@core/stream/conversation-stream-client';
import { STREAM_FETCH } from '@core/stream/stream-fetch.token';
import { provideChatMarkdown } from '../markdown/markdown-providers';
import { ConversationStore } from '../conversation-store';
import { TurnStore } from '../turn-store';
import { UploadStore } from '@core/documents/upload-store';
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
        // Pinned off rather than inherited from `public/config.json`: these specs
        // render fences and answers, and a deployment that switches diagrams or
        // math on would otherwise have them reach for a library jsdom cannot run.
        provideTestAppConfig({ features: { diagrams: false, math: false, rawStreamCodec: false } }),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: fetchMock },
        { provide: TokenService, useValue: { getToken: vi.fn().mockResolvedValue('token-1') } },
        { provide: Router, useValue: { navigateByUrl: vi.fn().mockResolvedValue(true) } },
        {
          provide: TurnSettingsStore,
          useValue: { streamSelection: () => SELECTION, applyConversationSettings },
        },
        UploadStore,
        ConversationStore,
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

      // agent-1 is mcp-1's child, so the timeline opens two top-level nodes
      // for three cards (US-502); the third card is inside the first.
      // US-504's footer closes the column; everything before it is a timeline
      // node. No reasoning region: the recorded turn's single `ReasoningDelta`
      // is the server's seed, which US-503 does not render.
      const content = host.querySelector('.assistant-turn__content');
      const sequence = [...(content?.children ?? [])].map((child) =>
        child.classList.contains('assistant-turn__footer') ? 'footer' : child.tagName.toLowerCase(),
      );
      expect(sequence).toEqual(['app-activity-card', 'app-activity-card', 'markdown', 'footer']);
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

  describe('nested activity work (US-502)', () => {
    /** Top-level cards only — the ones the timeline opened a node for. */
    function topLevelCards(): HTMLElement[] {
      return [
        ...host.querySelectorAll<HTMLElement>('.assistant-turn__content > app-activity-card'),
      ];
    }

    it('renders a child inside its parent card instead of opening a top-level node', async () => {
      await streamFullTurn();

      const top = topLevelCards();
      expect(
        top.map((card) => card.querySelector('.activity-card__name')?.textContent?.trim()),
      ).toEqual(['Andes Test MCP', 'Search Documents']);

      const nested = top[0]?.querySelectorAll('app-activity-card') ?? [];
      expect([...nested].map((card) => card.textContent)).toHaveLength(1);
      expect(top[0]?.textContent).toContain('Forecast Agent');
    });

    it('keeps a child whose parent scope was never seen at the top level', async () => {
      const handle = await startTurn();
      handle.enqueue(
        frame(
          assistantEvent('ActivityStarted', {
            scopeId: 'orphan-1',
            parentScopeId: 'a-scope-that-never-arrived',
            displayName: 'Orphaned Tool',
            source: undefined,
          }),
        ),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      // An unattached card is a rendering imperfection; a dropped one is a lost
      // tool call, so the fold roots it and the timeline still opens its node.
      expect(topLevelCards()).toHaveLength(1);
      expect(cardNames()).toEqual(['Orphaned Tool']);
    });

    it('steps the surface, radius and rail down through three levels', async () => {
      const handle = await startTurn();
      handle.enqueue(
        [
          assistantEvent('ActivityStarted', { scopeId: 'l0', displayName: 'Release Analyst' }),
          assistantEvent('ActivityStarted', {
            scopeId: 'l1',
            parentScopeId: 'l0',
            displayName: 'Doc Retriever',
            toolKind: 'Agent',
          }),
          assistantEvent('ActivityStarted', {
            scopeId: 'l2',
            parentScopeId: 'l1',
            displayName: 'Confluence Search',
          }),
          assistantEvent('ActivityStarted', {
            scopeId: 'l3',
            parentScopeId: 'l2',
            displayName: 'Even Deeper',
          }),
        ]
          .map(frame)
          .join(''),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(topLevelCards()).toHaveLength(1);
      const cards = [...host.querySelectorAll<HTMLElement>('.activity-card')];
      expect(cards.map((card) => card.className.replace('activity-card', '').trim())).toEqual([
        '',
        'activity-card--depth-1',
        'activity-card--depth-2',
        // The design draws three levels; a fourth reuses the third rather than
        // shrinking away, because an agent tree can nest arbitrarily.
        'activity-card--depth-2',
      ]);
    });
  });

  describe('copying a code block (US-603)', () => {
    async function streamCode(source: string): Promise<void> {
      const handle = await startTurn();
      handle.enqueue(
        [assistantEvent('TextDelta', { text: source }), assistantEvent('Finished')]
          .map(frame)
          .join(''),
      );
      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);
    }

    it('puts the exact block source on the clipboard, entities and all', async () => {
      await streamCode('```ts\nconst a = 1 & 2;\nif (a < 3) {\n  go();\n}\n```');

      host.querySelector<HTMLButtonElement>('.md-code__copy')?.click();
      await settle();

      // The source marked was given, not the rendered HTML and not the trailing
      // newline the renderer adds to close the block.
      expect(writeText).toHaveBeenCalledWith('const a = 1 & 2;\nif (a < 3) {\n  go();\n}');
    });

    it('confirms for two seconds, then returns to Copy', async () => {
      await streamCode('```ts\nconst a = 1;\n```');

      const button = host.querySelector<HTMLButtonElement>('.md-code__copy');
      button?.click();
      await settle();

      expect(button?.textContent).toBe('Copied');

      await settle(2000);

      expect(button?.textContent).toBe('Copy');
    });

    it('serves every block from one listener on the transcript', async () => {
      await streamCode('```ts\nfirst();\n```\n\n```ts\nsecond();\n```');

      const buttons = [...host.querySelectorAll<HTMLButtonElement>('.md-code__copy')];
      expect(buttons).toHaveLength(2);
      // Nothing is bound to the buttons themselves — they are innerHTML, with no
      // template position to bind at — so a click that never reaches the host
      // would copy nothing.
      buttons[1]?.click();
      await settle();

      expect(writeText).toHaveBeenCalledWith('second();');
      // Confirming one block ends the other's confirmation rather than leaving
      // two labels claiming the clipboard.
      buttons[0]?.click();
      await settle();

      expect(buttons[0]?.textContent).toBe('Copied');
      expect(buttons[1]?.textContent).toBe('Copy');
    });

    function copyStatus(): HTMLElement {
      return host.querySelector<HTMLElement>('.transcript__copy-status')!;
    }

    it('announces the copy in a live region, since the label alone is not enough', async () => {
      await streamCode('```ts\nconst a = 1;\n```');

      host.querySelector<HTMLButtonElement>('.md-code__copy')?.click();
      await settle();

      expect(copyStatus().textContent).toBe('Code block copied.');

      await settle(2000);

      expect(copyStatus().textContent).toBe('');
    });

    it('announces a second copy taken inside the first one’s window', async () => {
      await streamCode('```ts\nfirst();\n```\n\n```ts\nsecond();\n```');

      const buttons = [...host.querySelectorAll<HTMLButtonElement>('.md-code__copy')];
      buttons[0]?.click();
      await settle();

      // A live region announces a *transition*, not a value, so this watches the
      // DOM rather than reading it: clearing and re-setting the signal in one
      // task leaves the binding's previous value in place, nothing is written,
      // and a screen-reader user hears the first copy and nothing after it.
      let writes = 0;
      const observer = new MutationObserver((records) => {
        writes += records.length;
      });
      observer.observe(copyStatus(), { characterData: true, childList: true, subtree: true });

      buttons[1]?.click();
      await settle();
      observer.disconnect();

      // Cleared, then set again — without the clear reaching the DOM first this
      // is zero writes and the same text sitting there from the copy before.
      expect(writes).toBe(2);
      expect(copyStatus().textContent).toBe('Code block copied.');
      expect(buttons[1]?.textContent).toBe('Copied');
      expect(buttons[0]?.textContent).toBe('Copy');
    });

    it('keeps the accessible name on the visible label, per SC 2.5.3', async () => {
      await streamCode('```ts\nconst a = 1;\n```');

      const button = host.querySelector<HTMLButtonElement>('.md-code__copy');
      expect(button?.getAttribute('aria-label')).toBe('Copy ts code block');

      button?.click();
      await settle();

      expect(button?.getAttribute('aria-label')).toBe('Copied');

      await settle(2000);

      expect(button?.getAttribute('aria-label')).toBe('Copy ts code block');
    });

    it('survives the component being destroyed inside the confirmation window', async () => {
      await streamCode('```ts\nconst a = 1;\n```');

      host.querySelector<HTMLButtonElement>('.md-code__copy')?.click();
      await settle();
      fixture.destroy();

      // The timer would otherwise write into a detached button two seconds later.
      expect(() => vi.advanceTimersByTime(2000)).not.toThrow();
    });

    it('does nothing where the clipboard API does not exist at all', async () => {
      await streamCode('```ts\nconst a = 1;\n```');
      // An insecure context has no `navigator.clipboard`; the member access
      // throws inside the same `try` that catches a denial.
      Reflect.deleteProperty(navigator, 'clipboard');

      const button = host.querySelector<HTMLButtonElement>('.md-code__copy');
      button?.click();
      await settle();

      expect(button?.textContent).toBe('Copy');
    });

    it('leaves the control alone when the clipboard refuses', async () => {
      writeText.mockRejectedValueOnce(new Error('denied'));
      await streamCode('```ts\nconst a = 1;\n```');

      const button = host.querySelector<HTMLButtonElement>('.md-code__copy');
      button?.click();
      await settle();

      expect(button?.textContent).toBe('Copy');
    });

    it('ignores a click that is not a copy control', async () => {
      await streamCode('```ts\nconst a = 1;\n```');

      host.querySelector<HTMLElement>('.md-code__head')?.click();
      await settle();

      expect(writeText).not.toHaveBeenCalled();
    });
  });

  describe('copying a prompt (US-607)', () => {
    it('puts the prompt on the clipboard from the settled user bubble', async () => {
      await streamFullTurn();

      const footer = host.querySelector('.transcript__user-footer');
      expect(footer).not.toBeNull();
      footer?.querySelector<HTMLButtonElement>('button')?.click();
      await settle();

      expect(writeText).toHaveBeenCalledExactlyOnceWith('What is the weather?');
    });

    it('offers nothing on the optimistic bubble, which is mid-turn', async () => {
      await startTurn();

      // The prompt is on screen but the turn is not complete, so the row is the
      // pending bubble rather than a settled entry — and the criterion is about
      // a completed turn. The footer element is there and empty, holding the
      // height the row will keep once it settles.
      expect(host.querySelector('.transcript__bubble')?.textContent).toBe('What is the weather?');
      expect(host.querySelector('.transcript__user-footer')).not.toBeNull();
      expect(host.querySelector('.transcript__user-footer app-message-copy')).toBeNull();
    });

    it('serves the prompt control itself, not the delegated code listener', async () => {
      await streamFullTurn();

      // Both live inside the transcript host, and the delegated listener matches
      // on `.md-code__copy`. A prompt control reaching it would copy nothing —
      // there is no `.md-code` wrapper above it to read a block from — and a
      // control served by both would copy twice and announce a code block.
      const button = host.querySelector<HTMLButtonElement>('.transcript__user-footer button');
      expect(button).not.toBeNull();
      button?.click();
      await settle();

      expect(writeText).toHaveBeenCalledExactlyOnceWith('What is the weather?');
      expect(host.querySelector('.transcript__copy-status')?.textContent).toBe('');
    });
  });

  describe('the reasoning region (US-503)', () => {
    /** The opening pair every turn carries (streaming contract §4.3). */
    const SEED = 'Reviewing the request and preparing a response.\n\n';

    it('shows the region while reasoning streams, before any answer text', async () => {
      const handle = await startTurn();
      handle.enqueue(
        [
          assistantEvent('ReasoningDelta', { text: SEED }),
          assistantEvent('ReasoningDelta', { text: 'Weighing the options. ' }),
        ]
          .map(frame)
          .join(''),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      // The gap before the answer belongs to the ridgeline only while there is
      // nothing to read; the model's own reasoning takes it over.
      expect(host.querySelector('app-reasoning-region')).not.toBeNull();
      expect(host.querySelector('app-ridgeline')).toBeNull();
      // Still open, so no duration is claimed yet.
      expect(host.querySelector('.reasoning__duration')).toBeNull();
    });

    it('measures from the model’s first delta, not the server’s seed', async () => {
      const handle = await startTurn();
      handle.enqueue(frame(assistantEvent('ReasoningDelta', { text: SEED })));
      await settle(STREAM_BATCH_WINDOW_MS);
      // A second of stream latency that is not thinking time.
      await vi.advanceTimersByTimeAsync(1_000);
      handle.enqueue(frame(assistantEvent('ReasoningDelta', { text: 'Weighing the options. ' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      await vi.advanceTimersByTimeAsync(4_200 - STREAM_BATCH_WINDOW_MS);
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'The answer.' })));
      await settle(STREAM_BATCH_WINDOW_MS);

      const region = host.querySelector('app-reasoning-region');
      expect(region?.querySelector('.reasoning__duration')?.textContent?.trim()).toBe('4.2s');
      // Collapsed: the summary is not in the document until it is asked for.
      expect(region?.querySelector('.reasoning__body')).toBeNull();
      // Mid-stream the growing block is US-602's head/tail pair, and text this
      // short has no boundary yet — so it is the tail that carries it.
      expect(host.querySelector('.assistant-turn__content')?.textContent).toContain('The answer.');
    });

    it('expands to the model’s words only, and stays open for the rest of the turn', async () => {
      const handle = await startTurn();
      handle.enqueue(
        [
          assistantEvent('ReasoningDelta', { text: SEED }),
          assistantEvent('ReasoningDelta', { text: 'Weighing the options. ' }),
          assistantEvent('TextDelta', { text: 'The ' }),
        ]
          .map(frame)
          .join(''),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      const toggle = host.querySelector<HTMLButtonElement>('.reasoning__toggle');
      toggle?.click();
      await settle();

      const body = host.querySelector('.reasoning__body');
      expect(body?.textContent).toBe('Weighing the options. ');
      expect(body?.textContent).not.toContain('Reviewing the request');
      expect(toggle?.getAttribute('aria-controls')).toBe(body?.id);

      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'answer.' })));
      await settle(STREAM_BATCH_WINDOW_MS);

      // Still open: the live turn is one component instance for its duration.
      expect(host.querySelector('.reasoning__body')?.textContent).toContain('Weighing');
    });

    it('renders no region for a turn whose only reasoning was the seed', async () => {
      const handle = await startTurn();
      handle.enqueue(
        [
          assistantEvent('ReasoningDelta', { text: SEED }),
          assistantEvent('TextDelta', { text: 'Straight to it.' }),
        ]
          .map(frame)
          .join(''),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      // The seed is a server-authored status line, and US-406 already decided
      // this app renders none of those.
      expect(host.querySelector('app-reasoning-region')).toBeNull();
    });

    it('keeps the ridgeline for the gap when no reasoning is streaming', async () => {
      const handle = await startTurn();
      handle.enqueue(frame(assistantEvent('ReasoningDelta', { text: SEED })));
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(host.querySelector('app-ridgeline')).not.toBeNull();
      expect(host.querySelector('app-reasoning-region')).toBeNull();
    });

    it('renders no region for replayed history, which never stored one', async () => {
      store.bindRoute(CONVERSATION_ID);
      backend
        .expectOne(`${TEST_API_BASE_URL}/api/conversations/${CONVERSATION_ID}/messages`)
        .flush({
          id: CONVERSATION_ID,
          name: 'Conversation',
          messages: [
            { text: 'What is the weather?', role: CHAT_ROLE.user },
            { text: 'Sunny.', role: CHAT_ROLE.assistant },
          ],
        });
      await settle();

      expect(host.querySelector('.assistant-turn__md')?.textContent).toContain('Sunny.');
      expect(host.querySelector('app-reasoning-region')).toBeNull();
    });
  });

  describe('what a turn cost (US-504)', () => {
    it('shows the turn total in the message footer once Finished arrives', async () => {
      await streamFullTurn();

      expect(host.querySelector('.assistant-turn__usage')?.textContent?.trim()).toBe(
        '1,842 in · 512 out · 2,354 total',
      );
    });

    it('claims nothing for a turn that ended without Finished', async () => {
      const handle = await startTurn();
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Partial answer' })));
      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);

      // A cut-off turn never folded a Finished, so there is no total to claim —
      // and inventing one from the partial answer would be a fabricated figure.
      expect(host.querySelector('app-turn-notice-card')).not.toBeNull();
      expect(host.querySelector('.assistant-turn__usage')).toBeNull();
    });

    it('opens an activity onto its cost strip, and closes it again', async () => {
      const handle = await startTurn();
      handle.enqueue(
        [
          assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
          assistantEvent('ActivityCompleted', { scopeId: 'mcp-1' }),
        ]
          .map(frame)
          .join(''),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      const toggle = host.querySelector<HTMLButtonElement>('.activity-card__toggle');
      expect(toggle?.getAttribute('aria-expanded')).toBe('false');
      expect(host.querySelector('.activity-card__usage')).toBeNull();

      toggle?.click();
      await settle();

      expect(toggle?.getAttribute('aria-expanded')).toBe('true');
      const strip = host.querySelector('.activity-card__usage');
      expect(toggle?.getAttribute('aria-controls')).toBe(strip?.id);
      // No server attributes tokens per activity yet, so the duration is the
      // whole of the strip — the token columns arrive with the field.
      expect(strip?.textContent?.trim()).toBe('duration\u00a0\u00a01.4s');

      toggle?.click();
      await settle();

      expect(host.querySelector('.activity-card__usage')).toBeNull();
    });

    it('offers no chevron on an activity that is still running', async () => {
      const handle = await startTurn();
      handle.enqueue(frame(assistantEvent('ActivityStarted', { scopeId: 'mcp-1' })));
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(host.querySelector('.activity-card__toggle')).toBeNull();
    });
  });

  describe('an activity that failed (US-505)', () => {
    /** The state glyph in a card's *own* header, ignoring any nested card's. */
    function ownState(card: Element): string | null {
      const state = card.querySelector(
        ':scope > .activity-card > .activity-card__header > app-icon',
      );

      return state?.className.replace('activity-card__state', '').trim() ?? null;
    }

    it('marks the card failed, with a warning glyph and the duration in text', async () => {
      const handle = await startTurn();
      handle.enqueue(
        [
          assistantEvent('ActivityStarted', { scopeId: 'fn-1', toolKind: 'Function' }),
          assistantEvent('ActivityFailed', { scopeId: 'fn-1', toolKind: 'Function' }),
        ]
          .map(frame)
          .join(''),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(host.querySelector('.activity-card__state--warn')).not.toBeNull();
      expect(host.querySelector('.activity-card__duration')?.textContent?.trim()).toBe('0.6s');
      // Colour is never the only channel: the sr-only line carries both.
      expect(host.querySelector('.activity-card .visually-hidden')?.textContent).toBe(
        'Failed after 0.6s',
      );
    });

    it('leaves the parent of a failed child alone unless the parent failed too', async () => {
      const handle = await startTurn();
      handle.enqueue(
        [
          assistantEvent('ActivityStarted', { scopeId: 'agent-1', toolKind: 'Agent' }),
          assistantEvent('ActivityStarted', {
            scopeId: 'child-1',
            parentScopeId: 'agent-1',
            displayName: 'Confluence Search',
          }),
          assistantEvent('ActivityFailed', { scopeId: 'child-1' }),
          assistantEvent('ActivityCompleted', { scopeId: 'agent-1', toolKind: 'Agent' }),
        ]
          .map(frame)
          .join(''),
      );
      await settle(STREAM_BATCH_WINDOW_MS);

      const parent = host.querySelector('.assistant-turn__content > app-activity-card');
      const child = parent?.querySelector('app-activity-card');
      expect(ownState(parent!)).toBe('activity-card__state--ok');
      expect(ownState(child!)).toBe('activity-card__state--warn');
    });

    it('lets the answer complete normally, keeping the failed card in place', async () => {
      // The recorded turn fails fn-1 and then finishes: a failed tool call is
      // not a failed turn, and the card stays where it arrived.
      await streamFullTurn();

      expect(host.querySelector('.assistant-turn__md')?.textContent?.trim()).toBe('Hello, world.');
      expect(host.getAttribute('aria-busy')).toBeNull();
      expect(host.querySelector('app-turn-notice-card')).toBeNull();
      expect(cardNames()).toEqual(['Andes Test MCP', 'Forecast Agent', 'Search Documents']);
      expect(host.querySelector('.activity-card__state--warn')).not.toBeNull();
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
