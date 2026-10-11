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
 * Draws diagrams and typesets math, loading the libraries only when needed. Sits on `<markdown>`
 * because `ready` is the only signal its output is written. Settled renders only: the streaming
 * tail strips fence info, and redrawing SVG per flush would fight the batch cadence.
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

    // An SVG bakes its colours in, so a theme flip redraws it from the kept source `<pre>`.
    // `afterRenderEffect`, so a flip landing with a `[data]` change queries the updated tree.
    afterRenderEffect(() => {
      this._theme.theme();
      const drawn = [...this._element.querySelectorAll(`.${RENDERED}`)];
      // Nothing drawn means no theme change yet; a flip during the first pass is caught by the
      // generation bump, which makes that pass discard its results.
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
        // The tokenizer captured this before marked could rewrite its LaTeX escapes.
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

    // One at a time, so each diagram shows as it finishes rather than after the slowest.
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
    // DOMPurify strips Mermaid's `role`, so the figure is named and its loose `<text>` hidden;
    // the source stays reachable through the Copy control.
    figure.setAttribute('role', 'img');
    figure.setAttribute('aria-label', DIAGRAM_LABEL);
    // Its own profile: `sanitizeChatMarkdown` excludes `svg` by design.
    figure.append(sanitizeDiagramSvg(svg));
    figure.querySelector('svg')?.setAttribute('aria-hidden', 'true');

    wrapper.append(figure);
    // Last: the class hides the source, so an earlier failure leaves the block as it was.
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
