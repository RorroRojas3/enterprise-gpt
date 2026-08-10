/** How long to wait for the sprite before giving up and rendering without icons. */
const FETCH_TIMEOUT_MS = 10_000;

/**
 * Fetches the build-generated icon sprite and injects it into the document, so
 * `<use href="#bi-…">` resolves same-document.
 *
 * It has to be injected rather than referenced as `sprite.svg#bi-…`: an externally
 * referenced sprite is a separate document, and `currentColor` inside it resolves
 * against *that* document's initial colour — black — instead of inheriting from the
 * host element. Every icon would render black in both themes.
 *
 * Never rejects. An icon-less app is degraded; it is not a reason to route into the
 * fatal shell, which exists for a configuration that would make the app wrong rather
 * than plain.
 */
export async function loadIconSprite(): Promise<void> {
  // Against document.baseURI for the same reasons as loadAppConfig: sub-path
  // deployments, and a hard reload on a deep route.
  const url = new URL('icons/sprite.svg', document.baseURI);

  try {
    const response = await fetch(url, {
      credentials: 'omit',
      signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
    });
    if (!response.ok) {
      return;
    }

    const markup = await response.text();
    // A host with SPA fallback routing answers a missing file with index.html and a
    // 200, and injecting that would put a second <app-root> into the page.
    if (!markup.startsWith('<svg')) {
      return;
    }

    const host = document.createElement('div');
    host.setAttribute('aria-hidden', 'true');
    // Build-generated markup from our own origin, never user input.
    host.innerHTML = markup;
    document.body.prepend(host);
  } catch {
    // Offline, blocked, or timed out. Icons are absent; the app still works.
  }
}
