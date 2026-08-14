import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MarkdownComponent, MarkdownService } from 'ngx-markdown';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  AssistantStatusSnapshot,
  createInitialSnapshot,
  foldAssistantEvents,
} from '@domain/stream/andes/assistant-ui.contract';
import { TurnTimeline, createInitialTimeline, foldTimeline } from '@domain/stream/turn-timeline';
import { assistantEvent } from '@testing/stream-frames';
import { provideChatMarkdown } from '../markdown/markdown-providers';
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

describe('AssistantTurn markdown rendering (US-601, US-602)', () => {
  let fixture: ComponentFixture<AssistantTurn>;
  let host: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideChatMarkdown()] });
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
      // It must still look like code while it arrives: `_prism.scss` keys the
      // code surface off a `language-*` class, and the highlighter marks an
      // unlabelled block `language-none` on its way past. Without that the block
      // would render as bare text and then snap to a dark padded box at settle.
      expect(code?.className).toContain('language-');
      expect(tail?.querySelector('pre')?.className).toContain('language-');
    });

    it('highlights the same block once the turn settles', async () => {
      await show('Intro\n\n```ts\nconst a = 1;\n```', false);

      // `pre > code`, not any `code`: US-603's head bar names the language in a
      // `<code>` of its own, which comes first in document order.
      const code = host.querySelector('pre > code');
      expect(code?.className).toContain('language-ts');
      expect(host.querySelector('.token')).not.toBeNull();
    });
  });

  /**
   * US-602's closing criterion: whatever the split did mid-stream, the settled
   * turn must be the same render as one pass over the whole source.
   */
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
});
