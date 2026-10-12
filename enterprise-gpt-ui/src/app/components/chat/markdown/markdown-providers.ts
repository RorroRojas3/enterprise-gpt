import { Provider } from '@angular/core';
import DOMPurify from 'dompurify';
import {
  MARKED_EXTENSIONS,
  MARKED_OPTIONS,
  MarkedOptions,
  MarkedRenderer,
  SANITIZE,
  provideMarkdown,
} from 'ngx-markdown';
import { LazyRendererLoader } from './lazy-renderer-loader-service';
import { mathExtension } from './math-extension';
import './prism';

/**
 * The closed set of elements model markdown may become. `img` is absent: a remote image is a
 * third-party request and a beacon telling its host the answer was read.
 */
export const CHAT_ALLOWED_TAGS: readonly string[] = [
  'p',
  'br',
  'hr',
  'blockquote',
  'pre',
  'code',
  'h1',
  'h2',
  'h3',
  'h4',
  'h5',
  'h6',
  'ul',
  'ol',
  'li',
  'table',
  'thead',
  'tbody',
  'tr',
  'th',
  'td',
  'a',
  'strong',
  'em',
  'del',
  'input',
  // `renderer.code`'s chrome, which markdown syntax cannot produce. Safe only while
  // `renderer.html` drops raw HTML; markdown-security.spec drives a raw `<button>` to pin that.
  // `span` stays out: Prism highlights after sanitizing.
  'div',
  'button',
];

/**
 * Each traces to something marked renders; DOMPurify still rejects `javascript:` hrefs, and ARIA
 * survives through its `ALLOW_ARIA_ATTR` default. `tabindex` is for `renderer.code`'s scrollable
 * `<pre>` and, like `div`/`button`, is safe only while `renderer.html` drops raw HTML.
 */
export const CHAT_ALLOWED_ATTR: readonly string[] = [
  'href',
  'title',
  'class',
  'type',
  'checked',
  'disabled',
  'start',
  'align',
  'tabindex',
];

/** The DOM-boundary layer; config is per call, not `setConfig`, so no other caller widens it. */
export function sanitizeChatMarkdown(html: string): string {
  return DOMPurify.sanitize(html, {
    ALLOWED_TAGS: [...CHAT_ALLOWED_TAGS],
    ALLOWED_ATTR: [...CHAT_ALLOWED_ATTR],
    ALLOW_DATA_ATTR: false,
  });
}

/** Escapes text taken from markdown source before it re-enters an HTML string. */
function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** The language token of a fence's info string; the rest is dropped, never escaped into markup. */
const LANGUAGE = /^[\w+#.-]+/;

/** The fence info string `MarkdownExtras` renders as a diagram. */
export const DIAGRAM_LANGUAGE = 'mermaid';

/**
 * The parser layer. marked has no `html: false`, so `renderer.html` drops block and inline raw
 * HTML. Overrides must be own properties: `ngx-markdown` hands marked a spread of the renderer.
 */
export function chatMarkedOptionsFactory(): MarkedOptions {
  const renderer = new MarkedRenderer();
  renderer.html = () => '';

  // Named rather than hidden: a disabled control still needs a label, and the checkbox alone
  // carries done-ness.
  renderer.checkbox = ({ checked }) =>
    `<input aria-label="${checked ? 'Completed' : 'Not completed'}" disabled="" type="checkbox"${
      checked ? ' checked=""' : ''
    }>`;

  // The conversation title is the page's h1, so answer headings shift down one level.
  // A `function`: `this.parser` carries these options, whereas the static `Parser.parseInline`
  // would use marked's defaults and skip the raw-HTML suppression inside headings.
  renderer.heading = function (this: MarkedRenderer, { tokens, depth }) {
    const level = Math.min(depth + 1, 6);
    return `<h${level}>${this.parser.parseInline(tokens)}</h${level}>\n`;
  };

  // The sanitizer drops `img`; the alt text keeps the model's description, escaped as raw source.
  renderer.image = ({ text, title }) => escapeHtml(text || title || '');

  // The code chrome is emitted here, not grafted onto the DOM, so it stays off the streaming
  // tail's per-flush path. `language-*` goes on `<pre>` too: `_prism.scss` matches on it.
  renderer.code = ({ text, lang, escaped }) => {
    // A restricted character set, not escaping, so ` ```a"onload=… ` never reaches an attribute.
    const language = LANGUAGE.exec(lang ?? '')?.[0] ?? '';
    const marker = language === '' ? '' : ` class="language-${language}"`;
    const body = escaped ? text : escapeHtml(text);
    const label = language === '' ? '' : `<code class="md-code__lang">${language}</code>`;
    // Several "Copy" buttons are indistinguishable to a screen reader without the language.
    const name = language === '' ? 'Copy code block' : `Copy ${language} code block`;
    // Only marked: with the flag off or before `MarkdownExtras` loads it stays a code block, and
    // a diagram that fails to parse still shows its source.
    const diagram = language === DIAGRAM_LANGUAGE ? ' md-diagram' : '';
    // A wide block scrolls, so the `<pre>` takes `tabindex` for keyboard users (SC 2.1.1). It
    // stays unnamed: ARIA 1.2 prohibits naming its generic role, a `region` would make every
    // block a landmark, and the Copy control already carries the name.
    return (
      `<div class="md-code${diagram}"><div class="md-code__head">${label}` +
      `<button class="md-code__copy" type="button" aria-label="${name}">Copy</button></div>` +
      `<pre${marker} tabindex="0"><code${marker}>${body}\n</code></pre>` +
      `</div>\n`
    );
  };

  return {
    renderer,
    gfm: true,
    // Model output leans on single newlines for structure far more than prose markdown does.
    breaks: true,
    pedantic: false,
  };
}

/**
 * Chat markdown, sanitized at the parser and the DOM. Provided on `Chat`, not app config, so the
 * whole stack rides the lazy chunk; `scripts/check-initial-chunk.mjs` fails a static import.
 */
export function provideChatMarkdown(): Provider[] {
  return [
    ...provideMarkdown({
      markedOptions: { provide: MARKED_OPTIONS, useFactory: chatMarkedOptionsFactory },
      // Only a tokenizer captures math before marked rewrites it. Unconditional: with the flag
      // off its placeholder renders as plain text, and KaTeX stays behind the dynamic import.
      markedExtensions: [{ provide: MARKED_EXTENSIONS, multi: true, useFactory: mathExtension }],
      sanitize: { provide: SANITIZE, useValue: sanitizeChatMarkdown },
    }),
    // Bundled so a surface that renders answers cannot get one without the other.
    LazyRendererLoader,
  ];
}
