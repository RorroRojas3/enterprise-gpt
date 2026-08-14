import { describe, expect, it } from 'vitest';
import { CHAT_ALLOWED_TAGS, sanitizeChatMarkdown } from './markdown-providers';
import { sanitizeDiagramSvg } from './diagram-svg';

/** Serializes the fragment so an assertion can read what actually survived. */
function sanitize(svg: string): string {
  const host = document.createElement('div');
  host.append(sanitizeDiagramSvg(svg));

  return host.innerHTML;
}

/**
 * US-605. Mermaid's output is not markdown and never meets the markdown profile
 * — it is written after that profile has had its say, at the same point Prism's
 * own markup is. So it gets a profile of its own, and the first thing worth
 * pinning is that the two stayed separate.
 */
describe('diagram SVG sanitizing (US-605)', () => {
  it('leaves the markdown profile untouched', () => {
    // `svg` in `CHAT_ALLOWED_TAGS` would mean model text could mint one, and the
    // whole reason a diagram may carry an `<svg>` at all is that Mermaid — not
    // the model — produced it.
    for (const tag of ['svg', 'g', 'path', 'foreignObject', 'math', 'span']) {
      expect(CHAT_ALLOWED_TAGS).not.toContain(tag);
    }
  });

  it('keeps the elements a diagram is drawn from', () => {
    const html = sanitize(
      '<svg viewBox="0 0 10 10"><defs><marker id="a"><path d="M0 0"/></marker></defs>' +
        '<style>.n{fill:red}</style><g class="n"><rect width="4" height="4"/>' +
        '<text x="1" y="2">Start</text></g></svg>',
    );

    expect(html).toContain('<svg');
    expect(html).toContain('<marker');
    expect(html).toContain('<text');
    // Mermaid scopes its CSS to the render id and emits it inline; without the
    // `<style>` every diagram renders unstyled.
    expect(html).toContain('<style');
    expect(html).toContain('Start');
  });

  it('strips script, event handlers and foreignObject', () => {
    const html = sanitize(
      '<svg><script>alert(1)</script><g onclick="alert(2)" onload="alert(3)">' +
        '<foreignObject><img src=x onerror="alert(4)"></foreignObject></g></svg>',
    );

    expect(html).not.toContain('script');
    expect(html).not.toContain('onclick');
    expect(html).not.toContain('onload');
    expect(html).not.toContain('onerror');
    expect(html).not.toContain('foreignObject');
  });

  it('strips CSS that would reach off this origin', () => {
    // DOMPurify has no CSS parser: it passes a `<style>` body and a `style`
    // attribute through verbatim, and a `<style>` inside *inline* SVG is
    // document-scoped. Mermaid's `themeCSS` is directive-settable, so a
    // `%%{init}%%` in a model-authored fence reaches this text.
    const html = sanitize(
      '<svg><style>@import url("https://evil.test/x.css"); .n{background:url(https://evil.test/p)}' +
        '</style><rect style="fill:red;mask-image:url(https://evil.test/beacon)"/></svg>',
    );

    expect(html).not.toContain('@import');
    expect(html).not.toContain('evil.test');
  });

  it('keeps a local fragment reference, which is how a diagram finds its markers', () => {
    const html = sanitize('<svg><style>.e{marker-end:url(#arrow)}</style></svg>');

    expect(html).toContain('url(#arrow)');
  });

  it('leaves the markdown profile’s own behaviour untouched afterwards', () => {
    // The hooks are global to the DOMPurify instance `sanitizeChatMarkdown`
    // shares, so they have to come off again however this returns.
    sanitize('<svg><style>@import url("https://evil.test/x.css")</style></svg>');

    expect(sanitizeChatMarkdown('<p style="background:url(https://example.test/x)">x</p>')).toBe(
      '<p>x</p>',
    );
  });

  it('refuses links, however they are spelled', () => {
    // Mermaid's `click A "https://…"` syntax would otherwise mint a link the
    // user never wrote to an origin FR-51 says this client never contacts.
    const html = sanitize(
      '<svg><a href="https://example.test"><text>go</text></a>' +
        '<a xlink:href="javascript:alert(1)"><text>run</text></a></svg>',
    );

    expect(html).not.toContain('href');
    expect(html).not.toContain('example.test');
    expect(html).not.toContain('javascript:');
    // The label survives; only the navigation is taken away.
    expect(html).toContain('go');
  });
});
