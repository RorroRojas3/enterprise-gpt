const SHELL_ID = 'app-shell';
const HEADING_ID = 'app-shell-heading';
const STATUS_ID = 'app-shell-status';
const DETAIL_ID = 'app-shell-detail';
const RETRY_ID = 'app-shell-retry';
const FAILURE_HEADING = 'Enterprise GPT could not start';

/**
 * Switches `index.html`'s startup shell into its failure state. Runs after config or bootstrap
 * failed, so it depends on nothing Angular, the stylesheet, or the failed step would provide.
 *
 * @param reason A short, already user-facing explanation. Rendered as text.
 */
export function showFatalShell(reason: string): void {
  clearWatchdog();
  document.querySelector('app-root')?.remove();

  const shell = document.getElementById(SHELL_ID);
  if (!shell) {
    // A host replaced index.html without our markup; still avoid a blank page.
    document.body.textContent = `${FAILURE_HEADING}. ${reason}`;
    return;
  }

  shell.dataset['state'] = 'failed';

  const heading = document.getElementById(HEADING_ID);
  if (heading) {
    heading.textContent = FAILURE_HEADING;
    // Tells keyboard and screen-reader users the page changed; done before the alert role.
    heading.focus();
  }

  const detail = document.getElementById(DETAIL_ID);
  if (detail) {
    // textContent, never innerHTML: `reason` can carry a server-supplied status
    // line, and this page renders before any sanitizer exists.
    detail.textContent = reason;
  }

  // Applied last: setting the role before the content is in place tends to announce nothing.
  const status = document.getElementById(STATUS_ID);
  status?.setAttribute('role', 'alert');
  status?.setAttribute('aria-live', 'assertive');

  document.title = FAILURE_HEADING;

  // Not an inline onclick, so the page stays CSP-clean. A reload fixes the usual cause, a
  // transient 5xx or cold cache on config.json.
  document
    .getElementById(RETRY_ID)
    ?.addEventListener('click', () => location.reload(), { once: true });
}

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
