/**
 * Hands a URL to the browser as a download, through a detached anchor.
 *
 * A detached anchor rather than `location.href`: a navigation to a
 * `Content-Disposition: attachment` URL does work, but it also puts the URL in the
 * address bar for the instant before the browser cancels it, and in session history
 * afterwards. For a signed document link that is a credential leaked into history
 * (US-804); for a `blob:` URL it is a stale entry pointing at revoked memory.
 *
 * `rel="noopener"` because the anchor has no `target`, but a server-issued URL should
 * not be one `target` away from an opener reference either way.
 *
 * @param document The document to create the anchor in.
 * @param href The URL to download. Already validated by the caller.
 * @param fileName The name to save under. The server's `Content-Disposition` wins where
 *   it survives the request, which is why this is a suggestion rather than the source of
 *   truth for a cross-origin link.
 */
export function saveWithAnchor(document: Document, href: string, fileName: string): void {
  const anchor = document.createElement('a');
  anchor.href = href;
  anchor.download = fileName;
  anchor.rel = 'noopener';
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
}
