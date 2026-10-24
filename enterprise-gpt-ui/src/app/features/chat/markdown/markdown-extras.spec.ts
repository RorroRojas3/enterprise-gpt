import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MarkdownComponent } from 'ngx-markdown';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Theme } from '@core/theme/theme';
import { provideTestAppConfig } from '@testing/app-config';
import { DiagramRenderer, LazyRendererLoader, MathRenderer } from './lazy-renderer-loader';
import { MarkdownExtras } from './markdown-extras';
import { provideChatMarkdown } from './markdown-providers';

@Component({
  selector: 'app-extras-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownComponent, MarkdownExtras],
  template: `<markdown appMarkdownExtras [data]="source()" (ready)="rendered()" />`,
})
class ExtrasHost {
  readonly source = signal('');

  private _resolve: (() => void) | null = null;

  nextRender(): Promise<void> {
    return new Promise<void>((resolve) => {
      this._resolve = resolve;
    });
  }

  protected rendered(): void {
    this._resolve?.();
    this._resolve = null;
  }
}

const DIAGRAM = ['```mermaid', 'graph TD; A-->B', '```'].join('\n');
const SVG = '<svg data-drawn="yes"><g><text>A</text></g></svg>';

/**
 * Drives the real markdown pipeline, stubbing only the loader: Mermaid cannot lay out under jsdom.
 * Asserts structure and classes, never visibility, since Vitest loads no `_markdown.scss`.
 */
describe('MarkdownExtras', () => {
  let fixture: ComponentFixture<ExtrasHost>;
  let host: HTMLElement;
  let render: DiagramRenderer['render'] & ReturnType<typeof vi.fn>;
  let typeset: MathRenderer['render'] & ReturnType<typeof vi.fn>;

  function mount(features: { diagrams: boolean; math: boolean }): void {
    render = vi.fn<DiagramRenderer['render']>().mockResolvedValue(SVG);
    typeset = vi
      .fn<MathRenderer['render']>()
      .mockImplementation((source, display) => `<span class="katex">${display}:${source}</span>`);

    const loader: Pick<LazyRendererLoader, 'getDiagramRenderer' | 'getMathRenderer'> = {
      getDiagramRenderer: () =>
        Promise.resolve<DiagramRenderer | null>(features.diagrams ? { render } : null),
      getMathRenderer: () =>
        Promise.resolve<MathRenderer | null>(features.math ? { render: typeset } : null),
    };

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig({ features: { ...features, rawStreamCodec: false } }),
        provideChatMarkdown(),
        { provide: LazyRendererLoader, useValue: loader },
      ],
    });

    fixture = TestBed.createComponent(ExtrasHost);
    host = fixture.nativeElement as HTMLElement;
  }

  async function show(markdown: string): Promise<HTMLElement> {
    const rendered = fixture.componentInstance.nextRender();
    fixture.componentInstance.source.set(markdown);
    await fixture.whenStable();
    await rendered;
    // The directive still awaits the loader after the markdown has been written.
    await fixture.whenStable();
    await Promise.resolve();

    return host.querySelector('.md-diagram') as HTMLElement;
  }

  beforeEach(() => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  describe('with the flags off', () => {
    beforeEach(() => {
      mount({ diagrams: false, math: false });
    });

    it('renders a mermaid fence as a plain code block, with no error', async () => {
      const wrapper = await show(DIAGRAM);

      // No disabled branch: it is the same `.md-code` chrome any other fence gets.
      expect(wrapper.classList.contains('md-code')).toBe(true);
      expect(wrapper.classList.contains('md-diagram--rendered')).toBe(false);
      expect(wrapper.querySelector('svg')).toBeNull();
      expect(wrapper.querySelector('.md-diagram__notice')).toBeNull();
      expect(wrapper.querySelector('.md-code__lang')?.textContent).toBe('mermaid');
      expect(wrapper.querySelector('pre > code')?.textContent).toContain('graph TD; A-->B');
    });

    it('typesets nothing', async () => {
      await show('An expression: $$x^2$$');

      expect(typeset).not.toHaveBeenCalled();
    });
  });

  describe('with diagrams on', () => {
    beforeEach(() => {
      mount({ diagrams: true, math: false });
    });

    it('draws the diagram and keeps its source', async () => {
      const wrapper = await show(DIAGRAM);

      expect(render).toHaveBeenCalledExactlyOnceWith('graph TD; A-->B');
      expect(wrapper.classList.contains('md-diagram--rendered')).toBe(true);
      expect(wrapper.querySelector('.md-diagram__figure svg')).not.toBeNull();
      // Hidden by the class, not removed: it is the theme-flip fallback and what Copy copies.
      expect(wrapper.querySelector('pre > code')?.textContent).toContain('graph TD; A-->B');
    });

    it('shows the source and a notice when the diagram cannot be drawn', async () => {
      render.mockRejectedValue(new Error('Parse error'));
      const wrapper = await show(DIAGRAM);

      // The source was never removed, so the block stays the code block it was.
      expect(wrapper.classList.contains('md-diagram--rendered')).toBe(false);
      expect(wrapper.querySelector('svg')).toBeNull();
      expect(wrapper.querySelectorAll('.md-diagram__notice')).toHaveLength(1);
      expect(wrapper.querySelector('pre > code')?.textContent).toContain('graph TD; A-->B');
    });

    it('sanitizes what the renderer hands back before it reaches the DOM', async () => {
      render.mockResolvedValue('<svg><script>alert(1)</script><a href="http://x.test">t</a></svg>');
      const wrapper = await show(DIAGRAM);

      const figure = wrapper.querySelector('.md-diagram__figure');
      expect(figure?.querySelector('script')).toBeNull();
      expect(figure?.querySelector('a')).toBeNull();
      expect(figure?.innerHTML).not.toContain('x.test');
    });

    it('leaves an ordinary code block alone', async () => {
      await show('```ts\nconst a = 1;\n```');

      expect(render).not.toHaveBeenCalled();
      expect(host.querySelector('.md-diagram')).toBeNull();
    });

    it('draws the diagram again when the theme flips', async () => {
      const wrapper = await show(DIAGRAM);
      expect(render).toHaveBeenCalledOnce();

      // An SVG bakes its colours in, so unlike a code block it cannot re-tint.
      TestBed.inject(Theme).set('dark');
      await fixture.whenStable();
      await Promise.resolve();
      await fixture.whenStable();

      expect(render).toHaveBeenCalledTimes(2);
      expect(wrapper.querySelectorAll('.md-diagram__figure')).toHaveLength(1);
    });
  });

  describe('with math on', () => {
    beforeEach(() => {
      mount({ diagrams: false, math: true });
    });

    it('typesets an expression written mid-sentence', async () => {
      await show('An expression: $$x^2$$ inline.');

      // `$$` is display math anywhere; an unanchored block `start` would split this
      // paragraph, which `breaks: true` then joins with `<br>`.
      expect(typeset).toHaveBeenCalledExactlyOnceWith('x^2', true);
      expect(host.querySelector('.md-math')?.innerHTML).toContain('katex');
      expect(host.querySelector('markdown')?.textContent).toContain('An expression:');
      expect(host.querySelector('br')).toBeNull();
    });

    it('typesets a display expression written across lines', async () => {
      // A DOM pass cannot see this: `breaks: true` puts a `<br>` between the markers, and
      // auto-render only joins consecutive text nodes.
      await show('Result:\n\n$$\nE = mc^2\n$$\n');

      expect(typeset).toHaveBeenCalledExactlyOnceWith('E = mc^2', true);
      expect(host.querySelector('.md-math--display')).not.toBeNull();
    });

    it('typesets the LaTeX bracket forms, which marked would otherwise eat', async () => {
      // `\(`, `\)`, `\[` and `\]` are all in marked's inline escape class, so by
      // the time any DOM pass ran these would read `(x^2)` and `[y^2]`.
      await show('Inline \\(x^2\\) and display \\[y^2\\] here.');

      expect(typeset).toHaveBeenCalledWith('x^2', false);
      expect(typeset).toHaveBeenCalledWith('y^2', true);
      expect(host.querySelector('markdown')?.textContent).toContain('and display');
    });

    it('hands KaTeX the escapes LaTeX needs, not the ones marked leaves', async () => {
      // `\{`, `\}` and `\_` are stripped by marked's escape rule, so a DOM pass
      // would typeset a *different* expression and report nothing.
      await show('Set $$\\{1,2\\}$$ and $$a \\_ b$$.');

      expect(typeset).toHaveBeenCalledWith('\\{1,2\\}', true);
      expect(typeset).toHaveBeenCalledWith('a \\_ b', true);
    });

    it('leaves prose that merely mentions money alone', async () => {
      await show('It costs $5 to $10 depending on the plan.');

      // No single-`$` delimiter, so this is prose and stays prose.
      expect(typeset).not.toHaveBeenCalled();
      expect(host.querySelector('.md-math')).toBeNull();
    });

    it('leaves an expression inside a fenced block alone', async () => {
      await show('```txt\n$$x^2$$\n```');

      expect(typeset).not.toHaveBeenCalled();
      expect(host.querySelector('pre > code')?.textContent).toContain('$$x^2$$');
    });
  });

  describe('with math off', () => {
    beforeEach(() => {
      mount({ diagrams: false, math: false });
    });

    it('renders the expression as its own source, not as an error', async () => {
      await show('An expression: $$x^2$$ inline.');

      const expression = host.querySelector('.md-math');
      expect(expression?.textContent).toBe('x^2');
      expect(expression?.querySelector('.katex')).toBeNull();
    });
  });
});
