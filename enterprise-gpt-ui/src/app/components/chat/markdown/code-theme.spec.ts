import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { MarkdownHost, renderMarkdown } from '@testing/markdown-host';
import { provideChatMarkdown } from './markdown-providers';

/**
 * The Prism palette is tinted only from `--code-*`, so these pin the mechanism: no colour in the
 * markup and the same bytes under either theme. Contrast values are measured by
 * `scripts/check-tokens.mjs`, since jsdom resolves no custom properties.
 */
const FENCE = ['```ts', 'const answer = 42;', '```'].join('\n');

describe('code-block theming', () => {
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

    // Structure only: the radius and mono face are SCSS literals jsdom does not resolve.
    expect(wrapper).not.toBeNull();
    expect(wrapper?.querySelector('.md-code__head > .md-code__lang')?.textContent).toBe('ts');
    expect(wrapper?.querySelector('.md-code__head > .md-code__copy')?.textContent).toBe('Copy');
    // `_prism.scss` matches the marker on the `pre` as well as the `code`.
    expect(wrapper?.querySelector('pre')?.className).toContain('language-ts');
    expect(wrapper?.querySelector('pre > code')?.className).toContain('language-ts');
  });

  it('states no colour of its own, so the tokens are the only source', async () => {
    const { element } = await renderUnder('light');

    // An inline or baked-in colour is the only way a theme change could fail to re-tint.
    for (const node of element.querySelectorAll('.md-code, .md-code *')) {
      expect(node.getAttribute('style')).toBeNull();
    }
    expect(element.innerHTML).not.toMatch(/#[0-9a-f]{3,8}\b/i);
  });

  it('paints identical markup whether it is first created light or dark', async () => {
    // Each host is created under its theme, not flipped after, so both are first paints.
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

    // Re-highlighting or a stylesheet swap would have replaced the node or its markup.
    expect(element.querySelector('.md-code')).toBe(before);
    expect(element.innerHTML).toBe(markupBefore);
  });
});
