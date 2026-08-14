/**
 * Puts `text` on the clipboard, reporting whether it got there.
 *
 * The `navigator.clipboard` member access is inside the `try` on purpose: an
 * insecure context has no `clipboard` object at all, and that has to take the
 * same path as a permission the user refused. Neither is worth telling the
 * reader about beyond the confirmation not appearing — there is nothing they
 * could do about either — so callers use the return value to decide whether to
 * show one, and never to raise an error.
 */
export const COPY_LABEL = 'Copy';
export const COPIED_LABEL = 'Copied';

/**
 * How long a copy control stays in its confirmation state.
 *
 * Beside {@link copyText} rather than in each control, because three of them are on
 * screen at once — a code block's, a message footer's and the stopped card's — and
 * two confirmations of visibly different lengths read as one of them having failed.
 */
export const COPY_CONFIRMATION_MS = 2000;

export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
