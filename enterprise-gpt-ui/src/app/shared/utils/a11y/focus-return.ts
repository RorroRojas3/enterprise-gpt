/**
 * Records what has focus now and returns a function that puts it back. Ours rather than
 * `<dialog>`'s native restoration, so tests assert our code (the jsdom shim restores nothing).
 *
 * @returns A function that restores focus, or does nothing if the origin is gone.
 */
export function captureFocusOrigin(document: Document): () => void {
  const origin = document.activeElement;

  return () => {
    // Commonly gone: the overlay's action deleted the invoking row. The caller decides next.
    if (origin instanceof HTMLElement && origin.isConnected) {
      origin.focus();
    }
  };
}
