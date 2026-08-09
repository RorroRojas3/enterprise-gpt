const SHELL_ID = 'app-shell';
const HEADING_ID = 'app-shell-heading';
const STATUS_ID = 'app-shell-status';
const DETAIL_ID = 'app-shell-detail';
const RETRY_ID = 'app-shell-retry';
const FAILURE_HEADING = 'Enterprise GPT could not start';

/**
 * Switches the startup shell in `index.html` into its failure state.
 *
 * Runs when configuration loading or bootstrap has failed, so it must not depend
 * on Angular, on the stylesheet, or on anything the failed step was going to
 * provide. A blank page is the failure mode this exists to prevent.
 *
 * @param reason A short, already user-facing explanation. Rendered as text.
 */
export function showFatalShell(reason: string): void {
  clearWatchdog();
  document.querySelector('app-root')?.remove();

  const shell = document.getElementById(SHELL_ID);
  if (!shell) {
    // index.html was replaced by a host that does not carry our markup. Still not
    // a blank page.
    document.body.textContent = `${FAILURE_HEADING}. ${reason}`;
    return;
  }

  shell.dataset['state'] = 'failed';

  const heading = document.getElementById(HEADING_ID);
  if (heading) {
    heading.textContent = FAILURE_HEADING;
    // Moved before the alert role is applied so the region's content is already
    // final, and so a keyboard or screen-reader user is told the page changed.
    heading.focus();
  }

  const detail = document.getElementById(DETAIL_ID);
  if (detail) {
    // textContent, never innerHTML: `reason` can carry a server-supplied status
    // line, and this page renders before any sanitizer exists.
    detail.textContent = reason;
  }

  // Applied last, so the assistive-technology announcement fires on a region whose
  // content is already in place. Setting the role up front and mutating afterwards
  // is the common way this ends up announcing nothing at all.
  const status = document.getElementById(STATUS_ID);
  status?.setAttribute('role', 'alert');
  status?.setAttribute('aria-live', 'assertive');

  document.title = FAILURE_HEADING;

  // Wired here rather than as an inline onclick so the page stays CSP-clean. A
  // transient 5xx or a cold cache on config.json is the most common real cause,
  // and a reload fixes it.
  document
    .getElementById(RETRY_ID)
    ?.addEventListener('click', () => location.reload(), { once: true });
}

/**
 * Removes the startup shell once the application has rendered.
 */
export function hideStartupShell(): void {
  clearWatchdog();
  document.getElementById(SHELL_ID)?.remove();
}

function clearWatchdog(): void {
  const globals = globalThis as { __appShellWatchdog?: number };
  if (globals.__appShellWatchdog !== undefined) {
    clearTimeout(globals.__appShellWatchdog);
    delete globals.__appShellWatchdog;
  }
}
