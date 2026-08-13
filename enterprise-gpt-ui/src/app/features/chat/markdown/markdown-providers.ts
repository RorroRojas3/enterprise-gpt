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
