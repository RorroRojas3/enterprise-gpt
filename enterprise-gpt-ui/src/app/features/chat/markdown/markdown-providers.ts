import { Provider } from '@angular/core';
import DOMPurify from 'dompurify';
import {
  MARKED_OPTIONS,
  MarkedOptions,
  MarkedRenderer,
  SANITIZE,
  provideMarkdown,
} from 'ngx-markdown';
import './prism';

/**
 * The elements model-authored markdown is allowed to become (US-601).
 *
 * Every entry is something marked emits from markdown syntax; the list is the
 * closed set, so a tag that is not here cannot reach the DOM however it was
 * spelled in the source. `img` is deliberately absent: FR-51 says the client
 * issues no third-party requests at run time, and a remote image in a model
 * answer — or in a tool result the model is repeating — is both such a request
 * and a way to signal an external host that the answer was read.
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
  // US-603's code-block chrome, and the only two entries here that markdown
  // syntax does not produce: `renderer.code` emits them. Model text cannot,
  // because raw HTML never becomes markup at all — `renderer.html` drops it at
  // the parser.
  //
  // Note what that costs: these two are the one place the layers stop being
  // independent. Everything else here is safe whether or not the parser layer
  // holds; `div` and `button` are safe *because* it holds. A regression there —
  // a marked upgrade routing a token kind past `renderer.html`, or someone
  // relaxing the override — would let model text mint a `.md-code` wrapper that
  // the transcript's delegated copy listener would then serve. The
  // markdown-security spec drives a raw `<button>` through the real pipeline for
  // exactly that reason, so the regression fails a test rather than shipping.
  //
  // `span` stays out for the reason it always was: Prism highlights the DOM
  // *after* sanitizing, so it needs no allowance and a `<span>` in the source has
  // no business surviving.
  'div',
  'button',
];

/**
 * The attributes those elements may keep.
 *
 * Each one is traceable to a specific thing marked renders: `href`/`title` to
 * links, `class` to the `language-*` marker on a fenced block, `type`/`checked`/
 * `disabled` to GFM task lists, `start` to an ordered list that does not begin at
 * one, and `align` to a table column. Prism's own `span.token` markup needs no
 * allowance here — highlighting runs on the DOM *after* sanitizing, which is also
 * why `span` is not in {@link CHAT_ALLOWED_TAGS}.
 *
 * `href` being allowed is not the same as any href being allowed: DOMPurify's
 * default URI test still rejects `javascript:` and friends, dropping the attribute
 * and leaving the link text inert.
 *
 * ARIA attributes are deliberately absent from this list and survive anyway,
 * through DOMPurify's `ALLOW_ARIA_ATTR` default — which the task-list checkbox
 * below depends on for its label. Turning that default off would strip the label
 * silently, so the checkbox spec asserts it rather than trusting it.
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
];

/**
 * The second of US-601's two layers: whatever HTML survives parsing is filtered
 * against the profile above before it is written to the DOM.
 *
 * Configuration is passed per call rather than through `DOMPurify.setConfig`, so
 * this profile cannot leak onto — or be widened by — any other caller.
 */
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

/**
 * What a fence's info string may contribute to a `language-*` class. Anything
 * else — including the rest of an info string after the first token — is not a
 * language and is dropped rather than escaped into an attribute.
 */
const LANGUAGE = /^[\w+#.-]+/;

/**
 * The first of the two layers, and the reason the second is never asked to carry
 * the boundary alone.
 *
 * marked has no `html: false` switch, so raw HTML in the source is suppressed at
 * the parser instead: `html` is the one renderer marked routes both block-level
 * and inline raw-HTML tokens through, and returning an empty string drops them
 * outright. `<script>alert(1)</script>` never becomes markup at all, and an inline
 * `<em>x</em>` degrades to its text.
 *
 * The override has to be an *own* property of the renderer — `ngx-markdown` hands
 * marked a spread of the instance, which copies own properties and leaves the
 * prototype's defaults in place for every token kind not named here.
 */
export function chatMarkedOptionsFactory(): MarkedOptions {
  const renderer = new MarkedRenderer();
  renderer.html = () => '';

  // A task-list checkbox is a real, if disabled, form control, and a control with
  // no label is a failure however inert it is. It is named rather than hidden:
  // the checkbox is the *only* thing carrying done-ness — the text beside it
  // reads the same either way — so hiding it would take the state away from a
  // screen reader entirely.
  renderer.checkbox = ({ checked }) =>
    `<input aria-label="${checked ? 'Completed' : 'Not completed'}" disabled="" type="checkbox"${
      checked ? ' checked=""' : ''
    }>`;

  // Headings inside an answer are not headings of the page: the conversation
  // title is the h1, so a model's `#` would open a second one. Shifting by one
  // slots the answer's own hierarchy directly beneath the title without leaving
  // a gap — a jump from h1 straight to h3 is itself a heading-order failure.
  //
  // A `function`, not an arrow: marked binds the live renderer as `this`, and
  // `this.parser` is the parser carrying *these* options. The static
  // `Parser.parseInline` would build a fresh one with marked's defaults, quietly
  // exempting everything inside a heading from the raw-HTML suppression below.
  renderer.heading = function (this: MarkedRenderer, { tokens, depth }) {
    const level = Math.min(depth + 1, 6);
    return `<h${level}>${this.parser.parseInline(tokens)}</h${level}>\n`;
  };

  // Images are dropped by the sanitizer so the client issues no third-party
  // request (FR-51). Rendering the alt text keeps the description the model
  // wrote, instead of leaving an empty paragraph where content used to be — but
  // that text is raw source, so it is escaped here rather than handed to the
  // sanitizer as markup to judge.
  renderer.image = ({ text, title }) => escapeHtml(text || title || '');

  // A fenced block becomes frame 1b's chrome — a `--code-head` bar carrying the
  // language and a Copy control — rather than a bare `<pre>` (US-603). It is
  // emitted *here* rather than grafted onto the rendered DOM afterwards, which
  // is what keeps it out of the streaming tail's per-flush re-render path and
  // is what the story's first criterion requires.
  //
  // The `language-*` class goes on the `<pre>` as well as the `<code>`: that is
  // Prism's canonical markup, and `_prism.scss` matches on it.
  renderer.code = ({ text, lang, escaped }) => {
    // A language identifier, or nothing. Restricting the character set rather
    // than escaping it means the info string can never reach an attribute at
    // all — ` ```a"onload=… ` is simply not a language.
    const language = LANGUAGE.exec(lang ?? '')?.[0] ?? '';
    const marker = language === '' ? '' : ` class="language-${language}"`;
    const body = escaped ? text : escapeHtml(text);
    const label = language === '' ? '' : `<code class="md-code__lang">${language}</code>`;
    // An answer with six code blocks otherwise offers six controls all called
    // "Copy" — indistinguishable in a screen reader's list of buttons. The
    // language is the only thing that tells them apart, and ARIA attributes
    // survive the sanitizer through its `ALLOW_ARIA_ATTR` default.
    const name = language === '' ? 'Copy code block' : `Copy ${language} code block`;

    return (
      `<div class="md-code"><div class="md-code__head">${label}` +
      `<button class="md-code__copy" type="button" aria-label="${name}">Copy</button></div>` +
      `<pre${marker}><code${marker}>${body}\n</code></pre></div>\n`
    );
  };

  return {
    renderer,
    gfm: true,
    // Model output leans on single newlines for structure far more than prose
    // markdown does, and the transcript rendered them literally before US-601;
    // collapsing them now would silently reflow answers users had already read.
    breaks: true,
    pedantic: false,
  };
}

/**
 * Markdown rendering for the chat transcript, sanitized both at the parser and at
 * the DOM boundary (US-601).
 *
 * This is attached to the `Chat` component rather than to the application config
 * on purpose. `ngx-markdown`, marked, Prism and DOMPurify together are a
 * substantial dependency, the initial bundle has little headroom left under its
 * 720 kB ceiling, and only the chat route ever renders markdown — so the whole
 * renderer rides the lazy chat chunk. `scripts/check-initial-chunk.mjs` fails the
 * build if any of it is ever reachable from `main.ts` by a static import.
 */
export function provideChatMarkdown(): Provider[] {
  return provideMarkdown({
    markedOptions: { provide: MARKED_OPTIONS, useFactory: chatMarkedOptionsFactory },
    sanitize: { provide: SANITIZE, useValue: sanitizeChatMarkdown },
  });
}
