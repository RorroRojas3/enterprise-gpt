import DOMPurify from 'dompurify';

/**
 * DOMPurify passes CSS through verbatim, and a fence's `%%{init}%%` can set Mermaid's `themeCSS`,
 * so off-origin `url()` and `@import` are cut here. `url(#fragment)` stays for diagram markers.
 */
function stripRemoteCss(css: string): string {
  return css.replace(/@import[^;]*;?/gi, '').replace(/url\(\s*(['"]?)(?!#)[^)]*\1\s*\)/gi, 'none');
}

/**
 * A profile separate from `sanitizeChatMarkdown`'s, so widening one never widens the other. Drops
 * `a`/`href` (Mermaid `click` links) and `foreignObject` (HTML inside SVG); keeps `style`, which
 * Mermaid's inline scoped CSS needs, hence {@link stripRemoteCss}.
 */
export function sanitizeDiagramSvg(svg: string): DocumentFragment {
  DOMPurify.addHook('uponSanitizeElement', (node, data) => {
    if (data.tagName === 'style' && node.textContent !== null) {
      node.textContent = stripRemoteCss(node.textContent);
    }
  });
  DOMPurify.addHook('afterSanitizeAttributes', (node) => {
    const style = node.getAttribute?.('style');
    if (style != null) {
      node.setAttribute('style', stripRemoteCss(style));
    }
  });

  try {
    return DOMPurify.sanitize(svg, {
      USE_PROFILES: { svg: true, svgFilters: true },
      FORBID_TAGS: ['foreignObject', 'script', 'a'],
      FORBID_ATTR: ['href', 'xlink:href'],
      // A fragment: a second parse through `innerHTML` could revive neutralised markup.
      RETURN_DOM_FRAGMENT: true,
    });
  } finally {
    // Hooks are global to the DOMPurify instance `sanitizeChatMarkdown` shares.
    DOMPurify.removeHook('uponSanitizeElement');
    DOMPurify.removeHook('afterSanitizeAttributes');
  }
}
