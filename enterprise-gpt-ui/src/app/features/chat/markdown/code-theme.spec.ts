import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { MarkdownHost, renderMarkdown } from '@testing/markdown-host';
import { provideChatMarkdown } from './markdown-providers';

/**
 * Code has to stay legible in whichever theme the page is in, and this app does not
 * answer that with the usual two-stylesheet swap: `--code-bg` and `--code-head` are
 * fixed dark in both themes, so there is one hand-authored Prism palette tinted
 * entirely from custom properties and nothing to swap to.
 *
 * That makes the mechanism the thing worth pinning here, because it is what the two
 * remaining criteria rest on. If a code block carries no colour of its own, and if
 * it renders the same bytes whichever theme it was created under, then the values
 * `--code-*` resolve to are the only difference between the themes — so a change
 * re-tints on the same paint as `data-bs-theme` flipping, and there is no
 * light-styled first frame for a dark-theme reader to see.
 *
 * The *values* clearing 4.5:1 are measured by `scripts/check-tokens.mjs`, which is
 * where they can be: jsdom resolves no cascaded custom properties, so a spec
 * asserting a colour here would be asserting the empty string.
 */
const FENCE = ['```ts', 'const answer = 42;', '```'].join('\n');

describe('code-block theming (US-604)', () => {
  let originalTheme: string | undefined;

  beforeEach(() => {
    originalTheme = document.documentElement.dataset['bsTheme'];
    TestBed.configureTestingModule({ providers: [provideChatMarkdown()] });
  });

  afterEach(() => {
    if (originalTheme === undefined) {
      delete document.documentElement.dataset['bsTheme'];
    } else {
      document.documentElement.dataset['bsTheme'] = originalTheme;
    }
  });

  /** Renders a fence through a host **created** under `theme`, never re-themed after. */
  async function renderUnder(
    theme: 'light' | 'dark',
  ): Promise<{ fixture: ComponentFixture<MarkdownHost>; element: HTMLElement }> {
    document.documentElement.dataset['bsTheme'] = theme;
    const fixture = TestBed.createComponent(MarkdownHost);

    return { fixture, element: await renderMarkdown(fixture, FENCE) };
  }

  it('renders frame 1b’s chrome around the block', async () => {
    const { element } = await renderUnder('light');
    const wrapper = element.querySelector('.md-code');

    // Structure only. The 10px radius and the mono face criterion 1 also names
    // are literals in `_markdown.scss` and `_typography.scss`, which jsdom does
    // not resolve and no token check reaches.
    expect(wrapper).not.toBeNull();
    expect(wrapper?.querySelector('.md-code__head > .md-code__lang')?.textContent).toBe('ts');
    expect(wrapper?.querySelector('.md-code__head > .md-code__copy')?.textContent).toBe('Copy');
    // The marker on the `pre` as well as the `code` is Prism's canonical markup and
    // what `_prism.scss` matches on — the palette reaches nothing without it.
    expect(wrapper?.querySelector('pre')?.className).toContain('language-ts');
    expect(wrapper?.querySelector('pre > code')?.className).toContain('language-ts');
  });

  it('states no colour of its own, so the tokens are the only source', async () => {
    const { element } = await renderUnder('light');

    // An inline colour, or a hex baked into the markup, is a value the cascade
    // cannot reach — and it is the only way a theme change could fail to re-tint.
    for (const node of element.querySelectorAll('.md-code, .md-code *')) {
      expect(node.getAttribute('style')).toBeNull();
    }
    expect(element.innerHTML).not.toMatch(/#[0-9a-f]{3,8}\b/i);
  });

  it('paints identical markup whether it is first created light or dark', async () => {
    // Criterion 3. Each host is *created* under its theme rather than flipped
    // after rendering, so this is a first paint in both cases — which is the only
    // way the criterion's "no light-styled frame" can be observed at all.
    const light = await renderUnder('light');
    const dark = await renderUnder('dark');

    expect(dark.element.innerHTML).toBe(light.element.innerHTML);
  });

  it('re-tints in place when the theme flips, without re-rendering', async () => {
    const { fixture, element } = await renderUnder('light');
    const before = element.querySelector('.md-code');
    const markupBefore = element.innerHTML;

    document.documentElement.dataset['bsTheme'] = 'dark';
    await fixture.whenStable();

    // The same node, still carrying the same markup: nothing about a code block
    // depends on the theme except the values `--code-*` resolve to. A palette
    // that needed re-highlighting, or a stylesheet that had to be swapped in,
    // would have to replace one of the two.
    expect(element.querySelector('.md-code')).toBe(before);
    expect(element.innerHTML).toBe(markupBefore);
  });
});
