export const COPY_LABEL = 'Copy';
export const COPIED_LABEL = 'Copied';

/**
 * Shared by every copy control: several are on screen at once, and confirmations of different
 * lengths read as one of them having failed.
 */
export const COPY_CONFIRMATION_MS = 2000;

/**
 * Reports whether `text` reached the clipboard; callers only withhold the confirmation on false.
 * `navigator.clipboard` is read inside `try` so an insecure context fails like a refusal.
 */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
