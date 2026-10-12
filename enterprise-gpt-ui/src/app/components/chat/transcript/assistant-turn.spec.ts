import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MarkdownComponent, MarkdownService } from 'ngx-markdown';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  AssistantStatusSnapshot,
  createInitialSnapshot,
  foldAssistantEvents,
} from '@shared/models/andes/assistant-ui.contract';
import { MESSAGE_FEEDBACK_RATING } from '@shared/models/api/conversation.model';
import {
  TurnTimeline,
  createInitialTimeline,
  foldTimeline,
} from '@shared/utils/stream/turn-timeline';
import { provideChatMarkdown } from '@components/chat/markdown/markdown-providers';
import { provideTestAppConfig } from '@testing/app-config';
import { assistantEvent } from '@testing/stream-frames';
import { AssistantTurn } from './assistant-turn';

/** Renders one source string through the same pipeline, for canonical comparison. */
@Component({
  selector: 'app-markdown-reference',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownComponent],
  template: `<markdown [data]="source()" />`,
})
class MarkdownReference {
  readonly source = signal('');
}

const MESSAGE_ID = 'aaaaaaaa-1111-4222-8333-444444444444';

describe('AssistantTurn markdown rendering', () => {
  let fixture: ComponentFixture<AssistantTurn>;
  let host: HTMLElement;

  beforeEach(() => {
    // Pinned off rather than read from `public/config.json`: diagrams on would start a real
    // `import('mermaid')` jsdom cannot run. Drawing is `markdown-extras.spec.ts`'s subject.
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig({ features: { diagrams: false, math: false, rawStreamCodec: false } }),
        provideChatMarkdown(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    fixture = TestBed.createComponent(AssistantTurn);
    host = fixture.nativeElement as HTMLElement;
    document.body.append(host);
  });

  afterEach(() => {
    host.remove();
  });

  /** The folded state a turn would hold after streaming `text`. */
  function fold(text: string): { snapshot: AssistantStatusSnapshot; timeline: TurnTimeline } {
    const event = assistantEvent('TextDelta', { text });

    return {
      snapshot: foldAssistantEvents(createInitialSnapshot(), event),
      timeline: foldTimeline(createInitialTimeline(), event),
    };
  }

  /** The terminal frame, whose folded `Finished` clears `aria-busy`. */
  function finished() {
    return assistantEvent('Finished');
  }

  /** ngx-markdown assigns innerHTML one microtask after its input changes. */
  async function show(text: string, streaming: boolean): Promise<void> {
    const { snapshot, timeline } = fold(text);
    fixture.componentRef.setInput('snapshot', snapshot);
    fixture.componentRef.setInput('timeline', timeline);
    fixture.componentRef.setInput('streaming', streaming);
    fixture.detectChanges();
    await fixture.whenStable();
    await Promise.resolve();
    fixture.detectChanges();
    await fixture.whenStable();
    await Promise.resolve();
  }

  function renderers(): HTMLElement[] {
    return [...host.querySelectorAll<HTMLElement>('markdown')];
  }

  describe('the head/tail split', () => {
    it('renders two renderers while streaming and one once settled', async () => {
      await show('First block\n\nsecond bl', true);
      expect(renderers()).toHaveLength(2);

      await show('First block\n\nsecond block.', false);
      expect(renderers()).toHaveLength(1);
    });

    it('puts the closed blocks in the head and the growing text in the tail', async () => {
      await show('First block\n\nsecond bl', true);

      const [head, tail] = renderers();
      expect(head?.textContent).toContain('First block');
      expect(head?.textContent).not.toContain('second bl');
      expect(tail?.textContent).toContain('second bl');
    });

    it('carries the caret on the tail, and on the head when nothing is left over', async () => {
      await show('First block\n\nsecond bl', true);
      let [head, tail] = renderers();
      expect(head?.classList.contains('assistant-turn__md--caret')).toBe(false);
      expect(tail?.classList.contains('assistant-turn__md--caret')).toBe(true);

      // A closed fence at the very end leaves no tail, so the caret moves up.
      await show('Intro\n\n```ts\nconst a = 1;\n```', true);
      [head, tail] = renderers();
      expect(head?.classList.contains('assistant-turn__md--caret')).toBe(true);
      expect(tail?.classList.contains('assistant-turn__md--caret')).toBe(false);
    });

    it('drops the caret entirely once the turn settles', async () => {
      await show('All done.', false);

      expect(host.querySelector('.assistant-turn__md--caret')).toBeNull();
    });
  });

  describe('re-rendering', () => {
    it('re-parses only the tail until a block boundary is crossed', async () => {
      const parse = vi.spyOn(TestBed.inject(MarkdownService), 'parse');

      await show('Intro para\n\ngrow', true);
      parse.mockClear();

      // A delta inside the same block: the head string is unchanged, so the
      // renderer bound to it must not be asked to parse again.
      await show('Intro para\n\ngrowing', true);
      const sources = parse.mock.calls.map(([source]) => source);
      expect(sources).toContain('growing');
      expect(sources).not.toContain('Intro para\n\n');

      // Crossing a boundary moves text into the head, which must re-parse once.
      parse.mockClear();
      await show('Intro para\n\ngrowing\n\nnext', true);
      const after = parse.mock.calls.map(([source]) => source);
      expect(after).toContain('Intro para\n\ngrowing\n\n');
    });
  });

  describe('syntax highlighting', () => {
    it('leaves a still-arriving code block unhighlighted', async () => {
      await show('Intro\n\n```ts\nconst a = 1;', true);

      const tail = renderers()[1];
      const code = tail?.querySelector('code');
      expect(code).not.toBeNull();
      expect(code?.className).not.toContain('language-ts');
      expect(tail?.querySelector('.token')).toBeNull();
      // Still code while it arrives: `_prism.scss` keys the code surface off `language-*`,
      // which the highlighter sets to `language-none` on an unlabelled block.
      expect(code?.className).toContain('language-');
      expect(tail?.querySelector('pre')?.className).toContain('language-');
    });

    it('highlights the same block once the turn settles', async () => {
      await show('Intro\n\n```ts\nconst a = 1;\n```', false);

      // `pre > code`, not any `code`: the head bar names the language in a `<code>` of its
      // own, which comes first in document order.
      const code = host.querySelector('pre > code');
      expect(code?.className).toContain('language-ts');
      expect(host.querySelector('.token')).not.toBeNull();
    });
  });

  describe('deferred renderers', () => {
    it('marks a mermaid fence only once the turn settles', async () => {
      // Mid-stream the fence is in the tail, whose info strings `stripFenceInfo` removed.
      await show('Intro\n\n```mermaid\ngraph TD; A-->B', true);
      expect(host.querySelector('.md-diagram')).toBeNull();

      await show('Intro\n\n```mermaid\ngraph TD; A-->B\n```', false);
      expect(host.querySelector('.md-diagram')).not.toBeNull();
    });

    it('draws nothing in the streaming head, even where the fence has closed', async () => {
      // The head keeps its info string, so the wrapper is marked, but the live pair has no
      // `appMarkdownExtras`: the head is replaced whenever a block closes.
      await show('```mermaid\ngraph TD; A-->B\n```\n\nstill writ', true);

      const [head] = renderers();
      expect(head?.querySelector('.md-diagram')).not.toBeNull();
      expect(head?.querySelector('.md-diagram--rendered')).toBeNull();
      expect(head?.querySelector('.md-diagram__figure')).toBeNull();
    });
  });

  describe('the streaming answer as a live region', () => {
    function content(): HTMLElement | null {
      return host.querySelector('.assistant-turn__content');
    }

    it('is polite while streaming and silent once settled', async () => {
      await show('The forecast is', true);
      expect(content()?.getAttribute('aria-live')).toBe('polite');

      // Silent once settled, so the replacing entry does not announce the answer twice. The
      // call site is pinned in `transcript.spec.ts`.
      await show('The forecast is clear.', false);
      expect(content()?.getAttribute('aria-live')).toBeNull();
      expect(content()?.getAttribute('aria-busy')).toBeNull();
    });

    it('holds its announcements until the folded Finished releases them', async () => {
      await show('The forecast is', true);
      expect(content()?.getAttribute('aria-busy')).toBe('true');

      const { snapshot, timeline } = fold('The forecast is clear.');
      fixture.componentRef.setInput('snapshot', foldAssistantEvents(snapshot, finished()));
      fixture.componentRef.setInput('timeline', timeline);
      fixture.componentRef.setInput('streaming', true);
      fixture.detectChanges();
      await fixture.whenStable();

      // Still the streaming instance until the body closes, so the region is read as it stops
      // being busy. An explicit `false`, not a removed attribute: that release announces it.
      expect(content()?.getAttribute('aria-live')).toBe('polite');
      expect(content()?.getAttribute('aria-busy')).toBe('false');
    });
  });

  describe('the message footer', () => {
    let writeText: ReturnType<typeof vi.fn>;
    let clipboardDescriptor: PropertyDescriptor | undefined;

    beforeEach(() => {
      writeText = vi.fn().mockResolvedValue(undefined);
      clipboardDescriptor = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    });

    afterEach(() => {
      if (clipboardDescriptor !== undefined) {
        Object.defineProperty(navigator, 'clipboard', clipboardDescriptor);
      } else {
        Reflect.deleteProperty(navigator, 'clipboard');
      }
    });

    it('offers Copy on a settled turn that claims no token counts', async () => {
      await show('All done.', false);

      // Copy is offerable whether or not a `Finished` arrived; only the counts depend on it.
      expect(host.querySelector('.assistant-turn__footer')).not.toBeNull();
      expect(host.querySelector('app-message-copy')).not.toBeNull();
      expect(host.querySelector('.assistant-turn__usage')).toBeNull();
    });

    it('withholds the footer while the turn is still streaming', async () => {
      await show('Half an ans', true);

      expect(host.querySelector('.assistant-turn__footer')).toBeNull();
    });

    it('copies the original markdown, not the rendered text', async () => {
      const source = ['# Heading', '', '```ts', 'const a = 1;', '```'].join('\n');
      await show(source, false);

      host.querySelector<HTMLButtonElement>('app-message-copy button')?.click();
      await fixture.whenStable();

      // The rendered DOM has lost the fences and the hash by this point, and
      // gained Prism's markup — none of which is what a reader wants to paste.
      expect(writeText).toHaveBeenCalledExactlyOnceWith(source);
    });

    it('copies every block of a turn whose text was split by an activity', async () => {
      // The copy comes off the snapshot, not the per-node slices, so the whole answer survives
      // a tool call splitting it.
      const events = [
        assistantEvent('TextDelta', { text: 'Before. ' }),
        assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
        assistantEvent('ActivityCompleted', { scopeId: 'mcp-1' }),
        assistantEvent('TextDelta', { text: 'After.' }),
      ];
      const snapshot = events.reduce(foldAssistantEvents, createInitialSnapshot());
      const timeline = events.reduce(foldTimeline, createInitialTimeline());

      fixture.componentRef.setInput('snapshot', snapshot);
      fixture.componentRef.setInput('timeline', timeline);
      fixture.componentRef.setInput('streaming', false);
      fixture.detectChanges();
      await fixture.whenStable();

      host.querySelector<HTMLButtonElement>('app-message-copy button')?.click();
      await fixture.whenStable();

      expect(writeText).toHaveBeenCalledExactlyOnceWith('Before. After.');
    });
  });

  describe('the feedback controls', () => {
    /**
     * No persisted message id, nowhere to send a rating: that covers a server that sends no id
     * and a failed transcript write, whose `Finished` carries no metadata.
     */
    it('withholds the thumbs from an answer that never became a persisted message', async () => {
      await show('All done.', false);

      expect(host.querySelector('.assistant-turn__footer')).not.toBeNull();
      expect(host.querySelector('app-message-feedback')).toBeNull();
    });

    it('offers the thumbs beside Copy once the answer has an id', async () => {
      fixture.componentRef.setInput('messageId', MESSAGE_ID);
      await show('All done.', false);

      const footer = host.querySelector('.assistant-turn__footer');
      expect(footer?.querySelector('app-message-feedback')).not.toBeNull();

      // Copy, then the thumbs: the order a screen reader walks the footer in. No counts,
      // because this turn folded no `Finished`.
      const controls = [...(footer?.children ?? [])].map((child) => child.tagName.toLowerCase());
      expect(controls).toEqual(['app-message-copy', 'app-message-feedback']);
    });

    it('withholds them while the turn is still streaming, id or not', async () => {
      fixture.componentRef.setInput('messageId', MESSAGE_ID);
      await show('Half an ans', true);

      expect(host.querySelector('app-message-feedback')).toBeNull();
    });

    it('hands the stored rating down, so a reloaded answer shows it', async () => {
      fixture.componentRef.setInput('messageId', MESSAGE_ID);
      fixture.componentRef.setInput('rating', MESSAGE_FEEDBACK_RATING.down);
      await show('All done.', false);

      const pressed = [...host.querySelectorAll('app-message-feedback button')].map((thumb) =>
        thumb.getAttribute('aria-pressed'),
      );
      expect(pressed).toEqual(['false', 'true']);
    });

    it('reports the pressed thumb upward rather than acting on it', async () => {
      const emitted: string[] = [];
      fixture.componentInstance.rated.subscribe((rating) => emitted.push(rating));
      fixture.componentRef.setInput('messageId', MESSAGE_ID);
      await show('All done.', false);

      host.querySelector<HTMLButtonElement>('app-message-feedback button')?.click();

      expect(emitted).toEqual([MESSAGE_FEEDBACK_RATING.up]);
    });

    it('marks them refused while this answer has a rating in flight', async () => {
      fixture.componentRef.setInput('messageId', MESSAGE_ID);
      fixture.componentRef.setInput('ratingPending', true);
      await show('All done.', false);

      const thumbs = [...host.querySelectorAll<HTMLButtonElement>('app-message-feedback button')];
      expect(thumbs).toHaveLength(2);
      expect(thumbs.every((thumb) => thumb.getAttribute('aria-disabled') === 'true')).toBe(true);
    });
  });

  /** Whatever the split did mid-stream, the settled turn must match one pass over the source. */
  it('settles to a render character-identical to a single full render', async () => {
    const source = [
      '# Heading',
      '',
      'A paragraph with **bold** and `code`.',
      '',
      '```ts',
      'const answer: number = 42;',
      '```',
      '',
      '- one',
      '- two',
    ].join('\n');

    // Stream it in pieces first, so the comparison runs against a turn that
    // actually went through the head/tail split rather than a fresh mount.
    await show(source.slice(0, 20), true);
    await show(source.slice(0, 60), true);
    await show(source, false);

    const reference = TestBed.createComponent(MarkdownReference);
    document.body.append(reference.nativeElement as HTMLElement);
    reference.componentInstance.source.set(source);
    reference.detectChanges();
    await reference.whenStable();
    await Promise.resolve();
    reference.detectChanges();

    const settled = host.querySelector('markdown');
    const expected = (reference.nativeElement as HTMLElement).querySelector('markdown');

    expect(renderers()).toHaveLength(1);
    expect(settled?.innerHTML).toBe(expected?.innerHTML);

    (reference.nativeElement as HTMLElement).remove();
  });

  describe('a composed email', () => {
    const FENCED = [
      '```email',
      'To: alice@contoso.com',
      'Subject: Q3',
      '',
      'Hi Alice,',
      '```',
    ].join('\n');

    it('renders a marked email as a card, with the recipient on show', async () => {
      await show(FENCED, false);

      const card = host.querySelector('app-email-card');
      expect(card).not.toBeNull();
      expect(card?.textContent).toContain('alice@contoso.com');
      expect(card?.textContent).toContain('Q3');
    });

    it('leaves the prose around the block as markdown', async () => {
      await show(`Here you go:\n\n${FENCED}\n\nWant edits?`, false);

      expect(renderers()).toHaveLength(2);
      expect(host.querySelector('app-email-card')).not.toBeNull();
    });

    it('does not offer the footer control as well as the card', async () => {
      await show(FENCED, false);

      expect(host.querySelector('.assistant-turn__footer app-email-open-menu')).toBeNull();
    });

    it('waits for the block to close rather than showing half a card', async () => {
      await show('```email\nSubject: Q3\n\nHi Ali', true);

      expect(host.querySelector('app-email-card')).toBeNull();
    });

    it('offers the footer control for an email the model never marked', async () => {
      await show('Subject: Q3 review\n\nHi Alice,\n\nThanks.\n\nBest regards,', false);

      expect(host.querySelector('.assistant-turn__footer app-email-open-menu')).not.toBeNull();
      expect(host.querySelector('app-email-card')).toBeNull();
    });

    it('offers nothing on an answer that merely talks about email', async () => {
      await show('A good subject line is short and specific.', false);

      expect(host.querySelector('app-email-open-menu')).toBeNull();
      expect(host.querySelector('app-email-card')).toBeNull();
    });
  });
});

describe('AssistantTurn generated files', () => {
  let fixture: ComponentFixture<AssistantTurn>;
  let host: HTMLElement;

  const FILE = {
    id: '9c8b7a65-4321-4fed-8cba-0987654321fe',
    name: 'regional-summary.xlsx',
    extension: '.xlsx',
    mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    size: 2048,
  };

  async function render(attachments: readonly (typeof FILE)[]): Promise<void> {
    const event = assistantEvent('TextDelta', { text: 'Here it is.' });
    fixture.componentRef.setInput('snapshot', foldAssistantEvents(createInitialSnapshot(), event));
    fixture.componentRef.setInput('timeline', foldTimeline(createInitialTimeline(), event));
    fixture.componentRef.setInput('streaming', false);
    fixture.componentRef.setInput('attachments', attachments);
    fixture.componentRef.setInput('conversationId', '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b');
    fixture.detectChanges();
    await fixture.whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig({ features: { diagrams: false, math: false, rawStreamCodec: false } }),
        provideChatMarkdown(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    fixture = TestBed.createComponent(AssistantTurn);
    host = fixture.nativeElement as HTMLElement;
  });

  it('puts the files beside the answer, not in the hover-revealed footer', async () => {
    // The footer is invisible until hover or focus. A file the assistant made is part of the
    // answer, so it must not be somewhere a reader has to go looking.
    await render([FILE]);

    expect(host.querySelector('.assistant-turn__content > app-generated-files')).not.toBeNull();
    expect(host.querySelector('.assistant-turn__footer app-generated-files')).toBeNull();
  });

  it('renders no row at all for a turn that produced nothing', async () => {
    await render([]);

    expect(host.querySelector('app-generated-files')).toBeNull();
  });

  /**
   * The agent's own steps arrive as their own scopes beneath it, so a failed run renders the step that
   * failed nested inside the agent's card rather than as a flat line on it.
   */
  it('renders the failed step nested inside the agent that ran it', async () => {
    const events = [
      assistantEvent('ActivityStarted', {
        scopeId: 'agent-1',
        depth: 1,
        toolKind: 'Agent',
        displayName: 'File Agent',
      }),
      assistantEvent('ActivityStarted', {
        scopeId: 'step-1',
        parentScopeId: 'agent-1',
        depth: 2,
        toolKind: 'Agent',
        displayName: 'Running code',
      }),
      assistantEvent('ActivityProgress', {
        scopeId: 'step-1',
        parentScopeId: 'agent-1',
        depth: 2,
        toolKind: 'Agent',
        displayName: 'Running code',
        message: 'No file was produced',
      }),
      assistantEvent('ActivityFailed', {
        scopeId: 'step-1',
        parentScopeId: 'agent-1',
        depth: 2,
        toolKind: 'Agent',
        displayName: 'Running code',
        durationSeconds: 4,
      }),
      assistantEvent('ActivityFailed', {
        scopeId: 'agent-1',
        depth: 1,
        toolKind: 'Agent',
        displayName: 'File Agent',
        durationSeconds: 9,
      }),
    ];

    let snapshot = createInitialSnapshot();
    let timeline = createInitialTimeline();

    for (const event of events) {
      snapshot = foldAssistantEvents(snapshot, event);
      timeline = foldTimeline(timeline, event);
    }

    fixture.componentRef.setInput('snapshot', snapshot);
    fixture.componentRef.setInput('timeline', timeline);
    fixture.componentRef.setInput('streaming', false);
    fixture.componentRef.setInput('attachments', []);
    fixture.componentRef.setInput('conversationId', '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b');
    await fixture.whenStable();

    const child = host.querySelector('.activity-card__children .activity-card');

    expect(host.querySelectorAll('app-icon.activity-card__state--warn')).toHaveLength(2);
    expect(child?.querySelector('.activity-card__name')?.textContent?.trim()).toBe('Running code');
    expect(child?.querySelector('.activity-card__substatus')?.textContent?.trim()).toBe(
      'No file was produced',
    );
  });
});
