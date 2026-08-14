import { DestroyRef, Directive, ElementRef, afterRenderEffect, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ThemeService } from '@core/theme/theme-service';
import { MarkdownComponent } from 'ngx-markdown';
import { sanitizeDiagramSvg } from './diagram-svg';
import { LazyRendererLoader } from './lazy-renderer-loader';
import { MATH_CLASS, MATH_DISPLAY_CLASS } from './math-extension';

/** Marks a wrapper whose diagram has been drawn, so a re-render never redraws it. */
const RENDERED = 'md-diagram--rendered';

/** Marks an expression already typeset, for the same reason. */
const TYPESET = 'md-math--rendered';

/**
 * Draws the diagrams and typesets the math in a rendered answer, loading the
 * libraries that do it only when there is something for them to do (US-605).
 *
 * A directive on the `<markdown>` element rather than work inside
 * `AssistantTurn`, because `MarkdownComponent.ready` is the only signal that the
 * output has actually been written — it assigns `innerHTML` a beat after change
 * detection returns — and its `ElementRef` is not public, so the only way to
 * have both is to sit on the same element.
 *
 * **Only the settled render carries this.** The streaming tail cannot: US-602's
 * `stripFenceInfo` removes its info strings, so a mid-stream mermaid fence has no
 * language left to match. The head could, but its content is replaced wholesale
 * every time a block closes, and re-laying-out an SVG per flush would fight both
 * the batch cadence and US-606's `ResizeObserver`. So a diagram reads as a
 * `mermaid`-labelled code block while the answer arrives and becomes a picture
 * when it settles — the same contract US-602 already set for highlighting.
 */
@Directive({
  selector: 'markdown[appMarkdownExtras]',
})
export class MarkdownExtras {
  private readonly _element = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly _loader = inject(LazyRendererLoader);
  private readonly _theme = inject(ThemeService);

  private _destroyed = false;
  /** Bumped by anything that invalidates work in flight, so a stale pass bails. */
  private _generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this._destroyed = true;
    });

    inject(MarkdownComponent)
      .ready.pipe(takeUntilDestroyed())
      .subscribe(() => void this._enhance());

    // An SVG has its colours baked in, so unlike a code block it cannot re-tint
    // when `data-bs-theme` flips — it has to be drawn again. That is only
    // possible because the source `<pre>` is kept rather than removed, which
    // makes it the render cache as well as the failure fallback.
    //
    // `afterRenderEffect`, not `effect`: this reads and writes the DOM, and a
    // plain effect runs *before* Angular has updated it — so a flip landing in
    // the same pass as a `[data]` change would query the previous tree.
    afterRenderEffect(() => {
      this._theme.theme();
      const drawn = [...this._element.querySelectorAll(`.${RENDERED}`)];
      // Nothing drawn yet is every case that is not a theme *change*: the effect
      // runs once on creation, and doing the work then would load a renderer
      // before there was anything for it to render. A flip while the first pass
      // is still in flight is covered by the generation bump below, which makes
      // that pass discard its results and this one draw them.
      if (drawn.length === 0) {
        return;
      }

      this._generation++;
      for (const wrapper of drawn) {
        wrapper.classList.remove(RENDERED);
        for (const figure of wrapper.querySelectorAll('.md-diagram__figure')) {
          figure.remove();
        }
      }
      void this._enhance();
    });
  }

  private async _enhance(): Promise<void> {
    const generation = ++this._generation;
    const expressions = [...this._element.querySelectorAll<HTMLElement>(`.${MATH_CLASS}`)].filter(
      (node) => !node.classList.contains(TYPESET),
    );

    if (expressions.length > 0) {
      const math = await this._loader.getMathRenderer();
      if (!this._current(generation)) {
        return;
      }

      for (const expression of expressions) {
        if (!expression.isConnected) {
          continue;
        }
        // The source is what the tokenizer captured, before marked could rewrite
        // it — which is the only place a LaTeX escape still exists.
        expression.innerHTML =
          math?.render(
            expression.textContent ?? '',
            expression.classList.contains(MATH_DISPLAY_CLASS),
          ) ?? expression.innerHTML;
        expression.classList.add(TYPESET);
      }
    }

    const wrappers = [...this._element.querySelectorAll<HTMLElement>('.md-diagram')].filter(
      (wrapper) => !wrapper.classList.contains(RENDERED),
    );
    if (wrappers.length === 0) {
      return;
    }

    const diagrams = await this._loader.getDiagramRenderer();
    if (!this._current(generation) || diagrams === null) {
      return;
    }

    // Sequentially rather than all at once, so a long answer does not hold the
    // main thread for the sum of its diagrams before showing any of them. The
    // renderer serializes across directives itself, which is where Mermaid's
    // shared module state actually lives.
    for (const wrapper of wrappers) {
      const source = wrapper.querySelector('pre > code')?.textContent ?? '';
      try {
        const svg = await diagrams.render(source.replace(/\n$/, ''));
        if (!this._current(generation) || !wrapper.isConnected) {
          return;
        }
        this._draw(wrapper, svg);
      } catch {
        if (!this._current(generation) || !wrapper.isConnected) {
          return;
        }
        this._explain(wrapper);
      }
    }
  }

  private _draw(wrapper: HTMLElement, svg: string): void {
    wrapper.querySelector('.md-diagram__notice')?.remove();
    for (const stale of wrapper.querySelectorAll('.md-diagram__figure')) {
      stale.remove();
    }

    const figure = this._element.ownerDocument.createElement('figure');
    figure.className = 'md-diagram__figure';
    // DOMPurify strips `role` from Mermaid's own output, so the SVG arrives as an
    // unnamed graphic. Naming the figure — and hiding the loose `<text>` runs
    // inside it — is what leaves a screen reader something other than a bag of
    // fragments; the source stays reachable through the Copy control above it.
    figure.setAttribute('role', 'img');
    figure.setAttribute('aria-label', DIAGRAM_LABEL);
    // Sanitized through its own profile before it reaches the DOM. Mermaid's
    // output is not markdown, so it never met `sanitizeChatMarkdown` — and it
    // must not, since `svg` is absent from that profile by design.
    figure.append(sanitizeDiagramSvg(svg));
    figure.querySelector('svg')?.setAttribute('aria-hidden', 'true');

    wrapper.append(figure);
    // The class is what hides the source and restyles the wrapper. Setting it
    // last means a failure between here and the append leaves the block exactly
    // as it was rather than showing an empty frame.
    wrapper.classList.add(RENDERED);
  }

  private _explain(wrapper: HTMLElement): void {
    if (wrapper.querySelector('.md-diagram__notice') !== null) {
      return;
    }

    const notice = this._element.ownerDocument.createElement('p');
    notice.className = 'md-diagram__notice';
    notice.textContent = NOTICE;
    wrapper.append(notice);
  }

  /** False once destroyed or superseded, so no stale pass writes into the tree. */
  private _current(generation: number): boolean {
    return !this._destroyed && this._generation === generation;
  }
}

const NOTICE = 'This diagram could not be drawn. Its source is above.';
const DIAGRAM_LABEL = 'Diagram. Its source is available from the Copy control above.';
