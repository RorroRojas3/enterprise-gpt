/**
 * A detached anchor, not `location.href`, which would put the URL in session history: a leaked
 * credential for a signed link, a dead entry for a `blob:` URL.
 *
 * @param href Already validated by the caller.
 * @param fileName A suggestion: the server's `Content-Disposition` wins where it survives.
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
