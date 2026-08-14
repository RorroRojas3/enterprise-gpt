import DOMPurify from 'dompurify';

/**
 * Strips every CSS construct that can reach off this origin.
 *
 * DOMPurify has no CSS parser: it treats a `<style>` body and a `style` attribute
 * as opaque text and passes both through verbatim. So `@import url(…)` and
 * `background-image: url(https://…)` survive it untouched, and a `<style>` inside
 * *inline* SVG is document-scoped rather than SVG-scoped — which would make it a
 * third-party request (FR-51) and a page-wide restyle at once.
 *
 * This is not hypothetical for model-authored input: Mermaid's `themeCSS` is a
 * directive-settable key, so a `%%{init: {"themeCSS": "…"}}%%` inside the fence
 * reaches the emitted `<style>`. Mermaid's own guard is a brace-balance counter
 * that explicitly permits `url(https://…)`.
 *
 * A local `url(#fragment)` is kept, because that is how every diagram references
 * its own arrowhead markers and gradients.
 */
function stripRemoteCss(css: string): string {
  return css.replace(/@import[^;]*;?/gi, '').replace(/url\(\s*(['"]?)(?!#)[^)]*\1\s*\)/gi, 'none');
}

/**
 * The profile a rendered diagram is filtered against before it reaches the DOM
 * (US-605).
 *
 * Deliberately a **second, separate** DOMPurify call rather than an extension of
 * `sanitizeChatMarkdown`'s: the two inputs have nothing in common. That one
 * filters markdown the model wrote, against a closed list that contains no
 * `svg` and never will. This one filters an SVG document Mermaid produced, and
 * it runs at the point Prism's own markup does — *after* the markdown profile
 * has already had its say — so widening one could never widen the other.
 *
 * What is subtracted from DOMPurify's SVG profile matters more than what is in
 * it. `a`/`href` go because Mermaid's `click A "https://…"` syntax would
 * otherwise mint a link the user never wrote to an origin FR-51 says this client
 * never contacts. `foreignObject` goes because it is the one element that opens
 * an HTML document inside an SVG one; `htmlLabels: false` already stops Mermaid
 * emitting it, and this is the belt to that brace.
 *
 * `style` is **kept** — it is already in DOMPurify's SVG profile, and removing it
 * would leave every diagram unstyled, since Mermaid scopes its CSS to the render
 * id and emits it inline. Keeping it is what makes {@link stripRemoteCss}
 * necessary rather than optional.
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
      // A fragment rather than a string: assigning the result to `innerHTML`
      // would parse it a second time, and a second parse is a second chance for
      // markup the first one neutralised to come back.
      RETURN_DOM_FRAGMENT: true,
    });
  } finally {
    // Hooks are global to the DOMPurify instance, and `sanitizeChatMarkdown`
    // shares it. Removing them here is what keeps this profile from leaking onto
    // the markdown one — the same isolation the per-call config gives.
    DOMPurify.removeHook('uponSanitizeElement');
    DOMPurify.removeHook('afterSanitizeAttributes');
  }
}
