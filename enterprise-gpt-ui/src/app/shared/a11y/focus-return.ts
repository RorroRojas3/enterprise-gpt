/**
 * Records what has focus now and returns a function that puts it back.
 *
 * Implemented here rather than left to `<dialog>`'s native restoration for two
 * reasons: the acceptance criterion states the behaviour outright, so it should be
 * asserted against our code and not the browser's; and the test shim for jsdom
 * deliberately does not restore focus, which would otherwise make the assertion
 * circular.
 *
 * @param document The document whose `activeElement` to capture.
 * @returns A function that restores focus, or does nothing if the origin is gone.
 */
export function captureFocusOrigin(document: Document): () => void {
  const origin = document.activeElement;

  return () => {
    // The common case for a missing origin is real: the invoker was a row's kebab
    // button, and the action taken inside the overlay deleted the row. Focus falls to
    // <body> and the caller decides where it should go next.
    if (origin instanceof HTMLElement && origin.isConnected) {
      origin.focus();
    }
  };
}
