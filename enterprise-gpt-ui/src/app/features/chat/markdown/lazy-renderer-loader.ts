import { DOCUMENT, Injectable, InjectionToken, inject } from '@angular/core';
import type { KatexOptions } from 'katex';
import type { MermaidConfig } from 'mermaid';
import { APP_CONFIG } from '@core/config/app-config-token';

/** Turns one diagram source into an SVG document. */
export interface DiagramRenderer {
  /** @throws when the source does not parse, or fails while laying out. */
  render(source: string): Promise<string>;
}

/** Turns one LaTeX expression into HTML. */
export interface MathRenderer {
  /** Never throws: a malformed expression renders as its own source. */
  render(source: string, displayMode: boolean): string;
}

/** The slice of Mermaid's API this app uses. */
interface DiagramModule {
  initialize(config: MermaidConfig): void;
  parse(source: string, options: { suppressErrors: true }): Promise<unknown>;
  render(id: string, source: string): Promise<{ svg: string }>;
}

/** The slice of KaTeX's API this app uses. */
interface MathModule {
  renderToString(source: string, options: KatexOptions): string;
}

/**
 * A token so specs can swap the thunk: jsdom lacks the SVG measurement Mermaid's layout needs.
 * The literal `import()` stays in a real module so esbuild splits a chunk for it.
 */
export const DIAGRAM_MODULE = new InjectionToken<() => Promise<DiagramModule>>('DIAGRAM_MODULE', {
  providedIn: 'root',
  factory: () => async () => (await import('mermaid')).default,
});

export const MATH_MODULE = new InjectionToken<() => Promise<MathModule>>('MATH_MODULE', {
  providedIn: 'root',
  factory: () => async () => (await import('katex')).default,
});

/** Where `copy-katex.mjs` puts the stylesheet, relative to the app's base href. */
const KATEX_STYLESHEET = 'vendor/katex/katex.min.css';

/**
 * Loads Mermaid and KaTeX on first use only: together they dwarf the initial bundle.
 * `scripts/check-initial-chunk.mjs` fails the build if either is statically reachable from
 * `main.ts` or the chat chunk.
 */
@Injectable()
export class LazyRendererLoader {
  private readonly _features = inject(APP_CONFIG).features;
  private readonly _document = inject(DOCUMENT);
  private readonly _loadDiagramModule = inject(DIAGRAM_MODULE);
  private readonly _loadMathModule = inject(MATH_MODULE);

  private _diagram: Promise<DiagramRenderer | null> | null = null;
  private _math: Promise<MathRenderer | null> | null = null;

  /** The renderer, or null when diagrams are switched off or the chunk failed. */
  getDiagramRenderer(): Promise<DiagramRenderer | null> {
    if (!this._features.diagrams) {
      return Promise.resolve(null);
    }

    // Cleared on failure so a network blip does not disable diagrams for the session. Resolves
    // null rather than rejecting, because callers are fire-and-forget.
    this._diagram ??= this._createDiagramRenderer().catch((cause: unknown) => {
      console.error('The diagram renderer could not be loaded.', cause);
      this._diagram = null;
      return null;
    });

    return this._diagram;
  }

  /** The typesetter, or null when math is switched off or the chunk failed. */
  getMathRenderer(): Promise<MathRenderer | null> {
    if (!this._features.math) {
      return Promise.resolve(null);
    }

    this._math ??= this._createMathRenderer().catch((cause: unknown) => {
      console.error('The math renderer could not be loaded.', cause);
      this._math = null;
      return null;
    });

    return this._math;
  }

  private async _createDiagramRenderer(): Promise<DiagramRenderer> {
    const mermaid = await this._loadDiagramModule();
    let sequence = 0;
    // Serialized here, not per caller: Mermaid keeps config in module state and `parse` applies
    // each diagram's `%%{init}%%`, so interleaved renders across turns would swap settings.
    let queue: Promise<unknown> = Promise.resolve();

    const renderOne = async (source: string): Promise<string> => {
      // Parsed first so a failure never reaches `render`, whose error graphic would replace the
      // source that must stay on the page.
      mermaid.initialize(this._diagramConfig());
      if ((await mermaid.parse(source, { suppressErrors: true })) === false) {
        throw new Error('The diagram source could not be parsed.');
      }

      // Mermaid's temporary measuring element takes this id, so it must be unique on the page.
      const { svg } = await mermaid.render(`md-diagram-${++sequence}`, source);
      return svg;
    };

    return {
      render: (source: string) => {
        // Both arms, so one failure does not poison the queue; its caller still sees the rejection.
        const next = queue.then(
          () => renderOne(source),
          () => renderOne(source),
        );
        queue = next.catch(() => undefined);

        return next;
      },
    };
  }

  private async _createMathRenderer(): Promise<MathRenderer> {
    const katex = await this._loadMathModule();
    this._linkStylesheet();

    return {
      render: (source: string, displayMode: boolean) =>
        katex.renderToString(source, { ...this._mathOptions(), displayMode }),
    };
  }

  /** Mermaid's `base` theme fed live tokens, not transcribed hexes, so it follows the theme. */
  private _diagramConfig(): MermaidConfig {
    const styles = getComputedStyle(this._document.documentElement);
    const token = (name: string): string => styles.getPropertyValue(name).trim();

    const themeVariables: Record<string, string> = {};
    const set = (key: string, name: string): void => {
      const value = token(name);
      // Dropped rather than passed empty: Mermaid draws `fill: ""` as invisible nodes, and a
      // renamed token or a stylesheet-less test returns exactly that.
      if (value !== '') {
        themeVariables[key] = value;
      }
    };

    set('background', '--surface');
    set('primaryColor', '--bs-tertiary-bg');
    set('primaryTextColor', '--bs-body-color');
    set('textColor', '--bs-body-color');
    set('primaryBorderColor', '--bs-border-color');
    set('nodeBorder', '--bs-border-color');
    set('lineColor', '--bs-secondary-color');
    set('secondaryColor', '--think-bg');
    set('tertiaryColor', '--bs-secondary-bg');

    return {
      startOnLoad: false,
      // Never 'loose', and never 'sandbox': its iframe breaks sizing and theming.
      securityLevel: 'strict',
      // `<text>` labels, so no `<foreignObject>`; the cost is that labels do not wrap.
      htmlLabels: false,
      flowchart: { htmlLabels: false },
      // Model output is attacker-influenceable and layout is main-thread work, so cap one answer.
      maxTextSize: 20_000,
      maxEdges: 200,
      // Errors are this service's to report, in the notice beside the source.
      suppressErrorRendering: true,
      logLevel: 'fatal',
      deterministicIds: false,
      theme: 'base',
      themeVariables,
      fontFamily: styles.getPropertyValue('--bs-font-sans-serif').trim() || 'Inter, sans-serif',
    };
  }

  private _mathOptions(): KatexOptions {
    // Omitted when unresolved: KaTeX takes `errorColor: ''` literally, hiding malformed math.
    const fail = getComputedStyle(this._document.documentElement)
      .getPropertyValue('--bs-danger')
      .trim();

    return {
      output: 'htmlAndMathml',
      // A malformed expression renders as its own source rather than aborting the other ones.
      throwOnError: false,
      ...(fail === '' ? {} : { errorColor: fail }),
      // `true` enables \href, \url and \includegraphics: links and requests the user never wrote.
      trust: false,
      // Model output is full of Unicode KaTeX would otherwise warn about.
      strict: false,
      maxExpand: 100,
      maxSize: 50,
      // Fresh per call: KaTeX mutates it, so a shared one would leak \newcommand across answers.
      macros: {},
    };
  }

  /**
   * Injected on demand, not bundled: `styles.scss` is near its budget and most deployments leave
   * math off. `copy-katex.mjs` serves the file same-origin from `public/vendor/katex`.
   */
  private _linkStylesheet(): void {
    // Checked against the document: this service is route-scoped, so a field resets per visit.
    if (this._document.head.querySelector(`link[href$="${KATEX_STYLESHEET}"]`) !== null) {
      return;
    }

    const link = this._document.createElement('link');
    link.rel = 'stylesheet';
    link.href = new URL(KATEX_STYLESHEET, this._document.baseURI).href;
    // `public/vendor` comes from `npm run assets`, not git; otherwise unstyled math is silent.
    link.addEventListener('error', () =>
      console.error(`The KaTeX stylesheet is missing: ${link.href}. Run \`npm run assets\`.`),
    );
    this._document.head.append(link);
  }
}
