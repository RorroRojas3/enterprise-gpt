import { DOCUMENT, Injectable, InjectionToken, inject } from '@angular/core';
import { APP_CONFIG } from '@core/config/app-config.token';
import type { KatexOptions } from 'katex';
import type { MermaidConfig } from 'mermaid';

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
 * The dynamic import itself, behind a token so a spec can swap it.
 *
 * The literal `import()` has to stay in a real module for esbuild to split a
 * chunk for it; injecting the *thunk* is what keeps Mermaid out of the test
 * process, where it cannot run at all — jsdom implements none of the SVG
 * measurement APIs its layout depends on.
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
 * Loads the diagram and math renderers on first use, and never otherwise
 * (US-605).
 *
 * Provided by `provideChatMarkdown()` rather than in the root injector, for the
 * reason that function already gives about the markdown stack: only the chat
 * route renders answers, and both libraries together are several times the whole
 * initial bundle. `scripts/check-initial-chunk.mjs` fails the build if either
 * becomes statically reachable — from `main.ts` *or* from the chat chunk, since
 * a lazy chunk everyone downloads on the way to chat is the cost this story
 * exists to avoid.
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

    // Memoised so an answer with six diagrams loads Mermaid once; cleared on
    // failure so a network blip does not disable diagrams for the session.
    // Resolving rather than rejecting, because the caller is fire-and-forget and
    // a rejection there is an unhandled rejection.
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
    // Serialized *here*, not in the caller. Mermaid holds its configuration in
    // module state and `parse` re-applies the current diagram's own `%%{init}%%`
    // directives, so two interleaved renders can swap each other's settings —
    // and a transcript with diagrams in three replayed turns mounts three
    // directives at once, all sharing this one renderer. A queue in the caller
    // would only order that caller's own work.
    let queue: Promise<unknown> = Promise.resolve();

    const renderOne = async (source: string): Promise<string> => {
      // Parsed first, and a failure never reaches `render`. Mermaid's own failure
      // mode is to replace the element with an error graphic, which would take
      // the source off the page — and the source staying put is exactly what the
      // story's fourth criterion asks for.
      mermaid.initialize(this._diagramConfig());
      if ((await mermaid.parse(source, { suppressErrors: true })) === false) {
        throw new Error('The diagram source could not be parsed.');
      }

      // Mermaid appends a temporary measuring element carrying this id to the
      // document, so it has to be unique across every turn on screen.
      const { svg } = await mermaid.render(`md-diagram-${++sequence}`, source);
      return svg;
    };

    return {
      render: (source: string) => {
        // Both arms, so one diagram's failure does not poison the queue for the
        // next — the rejection still reaches this caller.
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

  /**
   * Mermaid's `base` theme with this app's own tokens, read live rather than
   * transcribed: `check-tokens.mjs` exists because duplicated hexes drift, and a
   * diagram built from `getComputedStyle` matches whatever `data-bs-theme`
   * currently resolves to with nothing new for that check to police.
   */
  private _diagramConfig(): MermaidConfig {
    const styles = getComputedStyle(this._document.documentElement);
    const token = (name: string): string => styles.getPropertyValue(name).trim();

    const themeVariables: Record<string, string> = {};
    const set = (key: string, name: string): void => {
      const value = token(name);
      // Dropped rather than passed empty. Mermaid given `fill: ""` draws
      // invisible nodes, and an empty value is exactly what a renamed token —
      // or a test environment with no stylesheet — hands back.
      if (value !== '') {
        themeVariables[key] = value;
      }
    };

    set('background', '--surface');
    set('primaryColor', '--surface-2');
    set('primaryTextColor', '--bs-body-color');
    set('textColor', '--bs-body-color');
    set('primaryBorderColor', '--bs-border-color');
    set('nodeBorder', '--bs-border-color');
    set('lineColor', '--muted');
    set('secondaryColor', '--think-bg');
    set('tertiaryColor', '--active-bg');

    return {
      // Mermaid must never scan the document on its own; every render in this
      // app is one this service asked for, on one element it owns.
      startOnLoad: false,
      // Labels HTML-escaped and `click` callbacks disabled. Never 'loose', and
      // never 'sandbox' — that renders into an iframe, which breaks sizing and
      // theming both.
      securityLevel: 'strict',
      // Forces `<text>` labels, so no `<foreignObject>` and therefore no HTML
      // document inside the SVG one. The cost is that labels do not wrap.
      htmlLabels: false,
      flowchart: { htmlLabels: false },
      // Model output is attacker-influenceable through uploaded documents and
      // tool results, and diagram layout is main-thread work. These are the
      // ceilings on what one answer can spend.
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
    // The default rather than an empty string when `--fail` cannot be resolved:
    // KaTeX takes `errorColor: ''` literally and renders a malformed expression
    // indistinguishably from a good one, which is the failure this option exists
    // to make visible.
    const fail = getComputedStyle(this._document.documentElement).getPropertyValue('--fail').trim();

    return {
      output: 'htmlAndMathml',
      // The math half of criterion 4: a malformed expression renders as its own
      // source in the error colour, in place, rather than aborting the pass and
      // taking the answer's other expressions with it.
      throwOnError: false,
      ...(fail === '' ? {} : { errorColor: fail }),
      // Stated rather than defaulted. `true` enables \href, \url and
      // \includegraphics — a link the user never wrote, to an origin FR-51 says
      // this client never contacts.
      trust: false,
      // Model output is full of Unicode KaTeX would otherwise warn about.
      strict: false,
      maxExpand: 100,
      maxSize: 50,
      // A fresh object per call: KaTeX mutates the macros it is given, so a
      // shared one would carry an answer's \newcommand into the next answer.
      macros: {},
    };
  }

  /**
   * KaTeX's stylesheet is injected on demand rather than bundled: `styles.scss`
   * is one eagerly-loaded file with little headroom under its budget, and math
   * is a flag most deployments leave off. `copy-katex.mjs` puts the file and its
   * faces under `public/vendor/katex`, so this is same-origin (FR-51).
   */
  private _linkStylesheet(): void {
    // Checked against the document, not a field: this service is scoped to the
    // chat route, so leaving and returning would otherwise append a second link
    // per visit.
    if (this._document.head.querySelector(`link[href$="${KATEX_STYLESHEET}"]`) !== null) {
      return;
    }

    const link = this._document.createElement('link');
    link.rel = 'stylesheet';
    link.href = new URL(KATEX_STYLESHEET, this._document.baseURI).href;
    // `public/vendor` is filled by `npm run assets`, not committed, so a
    // deployment that ships `dist` without it renders math unstyled — worse than
    // not rendering it, and silent without this.
    link.addEventListener('error', () =>
      console.error(`The KaTeX stylesheet is missing: ${link.href}. Run \`npm run assets\`.`),
    );
    this._document.head.append(link);
  }
}
