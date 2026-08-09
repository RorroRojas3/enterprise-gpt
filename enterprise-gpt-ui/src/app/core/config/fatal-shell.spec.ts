import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { hideStartupShell, showFatalShell } from './fatal-shell';

const SHELL_MARKUP = `
  <app-root></app-root>
  <div id="app-shell" data-state="starting">
    <div class="card">
      <h1 tabindex="-1" id="app-shell-heading">Starting Enterprise GPT…</h1>
      <div class="on-failed">
        <div id="app-shell-status">
          <code id="app-shell-detail"></code>
        </div>
        <button type="button" id="app-shell-retry">Try again</button>
      </div>
    </div>
  </div>
`;

function shell(): HTMLElement | null {
  return document.getElementById('app-shell');
}

beforeEach(() => {
  document.body.innerHTML = SHELL_MARKUP;
  document.title = 'Enterprise GPT';
  (globalThis as { __appShellWatchdog?: number }).__appShellWatchdog = undefined;
});

afterEach(() => {
  document.body.innerHTML = '';
  vi.restoreAllMocks();
});

describe('showFatalShell', () => {
  it('switches the shell into its failure state and drops the unbootstrapped app root', () => {
    showFatalShell('config.json responded 500.');

    expect(shell()?.dataset['state']).toBe('failed');
    expect(document.querySelector('app-root')).toBeNull();
  });

  it('renders the reason as text', () => {
    showFatalShell('"apiBaseUrl" must be an absolute URL.');

    expect(document.getElementById('app-shell-detail')?.textContent).toBe(
      '"apiBaseUrl" must be an absolute URL.',
    );
  });

  it('does not interpret markup in the reason', () => {
    // The reason can carry a server-supplied status line, and this page renders
    // before any sanitizer exists.
    showFatalShell('<img src=x onerror=alert(1)>');

    const detail = document.getElementById('app-shell-detail');
    expect(detail?.innerHTML).not.toContain('<img');
    expect(detail?.querySelector('img')).toBeNull();
    expect(detail?.textContent).toBe('<img src=x onerror=alert(1)>');
  });

  it('applies the alert role only after the content is final', () => {
    // An alert region announces on content insertion. Setting the role first and
    // mutating afterwards is how these end up announcing nothing.
    const observed: (string | null)[] = [];
    const status = document.getElementById('app-shell-status') as Node;
    new MutationObserver(() =>
      observed.push(document.getElementById('app-shell-status')?.getAttribute('role') ?? null),
    ).observe(status, { childList: true, characterData: true, subtree: true });

    showFatalShell('config.json responded 503.');

    const region = document.getElementById('app-shell-status');
    expect(region?.getAttribute('role')).toBe('alert');
    expect(region?.getAttribute('aria-live')).toBe('assertive');
    expect(observed.every((role) => role === null)).toBe(true);
  });

  it('scopes the live region to the explanation, not the heading or the button', () => {
    // The heading already has focus, so including it would have it read twice, and
    // an interactive control does not belong inside a live region.
    showFatalShell('config.json responded 503.');

    const region = document.getElementById('app-shell-status');
    expect(shell()?.getAttribute('role')).toBeNull();
    expect(region?.querySelector('#app-shell-heading')).toBeNull();
    expect(region?.querySelector('#app-shell-retry')).toBeNull();
  });

  it('moves focus to the heading and retitles the tab', () => {
    showFatalShell('config.json responded 503.');

    expect(document.activeElement?.id).toBe('app-shell-heading');
    expect(document.title).toBe('Enterprise GPT could not start');
  });

  it('reloads the page when the retry control is used', () => {
    const reload = vi.fn();
    vi.spyOn(globalThis, 'location', 'get').mockReturnValue({
      ...location,
      reload,
    } as unknown as Location);

    showFatalShell('config.json responded 503.');
    document.getElementById('app-shell-retry')?.dispatchEvent(new MouseEvent('click'));

    expect(reload).toHaveBeenCalledOnce();
  });

  it('cancels the index.html watchdog so it cannot overwrite the real reason', () => {
    const clear = vi.spyOn(globalThis, 'clearTimeout');
    (globalThis as { __appShellWatchdog?: number }).__appShellWatchdog = 1234;

    showFatalShell('config.json is not valid JSON.');

    expect(clear).toHaveBeenCalledWith(1234);
  });

  it('falls back to body text when the shell markup is missing', () => {
    document.body.innerHTML = '<app-root></app-root>';

    expect(() => showFatalShell('config.json is not valid JSON.')).not.toThrow();
    expect(document.body.textContent).toContain('config.json is not valid JSON.');
  });
});

describe('hideStartupShell', () => {
  it('removes the shell once the app has rendered', () => {
    hideStartupShell();

    expect(shell()).toBeNull();
  });

  it('cancels the watchdog', () => {
    const clear = vi.spyOn(globalThis, 'clearTimeout');
    (globalThis as { __appShellWatchdog?: number }).__appShellWatchdog = 4321;

    hideStartupShell();

    expect(clear).toHaveBeenCalledWith(4321);
  });

  it('does not throw when the shell is already gone', () => {
    document.body.innerHTML = '';

    expect(() => hideStartupShell()).not.toThrow();
  });
});
