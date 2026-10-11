/** How long to wait for the sprite before giving up and rendering without icons. */
const FETCH_TIMEOUT_MS = 10_000;

/**
 * Inlined rather than referenced as `sprite.svg#bi-…`, where `currentColor` would resolve in the
 * external document and render every icon black. Never rejects: missing icons are not fatal.
 */
export async function loadIconSprite(): Promise<void> {
  // Against document.baseURI for sub-path deployments and hard reloads on a deep route.
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
