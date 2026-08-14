/**
 * KaTeX ships types for its main entry only; the `contrib` subpaths have none.
 *
 * Declared narrowly rather than as `any`: this is the whole of the surface
 * `LazyRendererLoader` uses, so a KaTeX upgrade that changed the signature is a
 * compile error here instead of a run-time one in a lazy chunk.
 */
declare module 'katex/contrib/auto-render' {
  import type { KatexOptions } from 'katex';

  interface AutoRenderDelimiter {
    left: string;
    right: string;
    display: boolean;
  }

  interface AutoRenderOptions extends KatexOptions {
    delimiters?: AutoRenderDelimiter[];
    ignoredTags?: string[];
    ignoredClasses?: string[];
    preProcess?: (math: string) => string;
  }

  export default function renderMathInElement(
    element: HTMLElement,
    options?: AutoRenderOptions,
  ): void;
}
