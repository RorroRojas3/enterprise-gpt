import { AppConfig, AppConfigError, assertAppConfig, normalizeAppConfig } from './app-config';

/** How long to wait for `config.json` before giving up and showing the fatal shell. */
const FETCH_TIMEOUT_MS = 10_000;

/** @throws {AppConfigError} When `config.json` is unreachable, not JSON, or invalid. */
export async function loadAppConfig(): Promise<AppConfig> {
  // Against document.baseURI: '/config.json' breaks a sub-path deployment, and
  // './config.json' breaks a hard reload on a deep route such as /chat/{id}.
  const url = new URL('config.json', document.baseURI);

  let response: Response;
  try {
    response = await fetch(url, {
      // A cached config is how one environment ends up pointed at another.
      cache: 'no-store',
      // config.json is public deployment metadata; never attach credentials, which
      // would leak cookies to a CDN origin.
      credentials: 'omit',
      // Without this, a wedged host leaves a blank page instead of the fatal shell.
      signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
    });
  } catch (cause) {
    throw new AppConfigError('The application configuration could not be reached.', { cause });
  }

  // Checked before reading the body: a host with SPA fallback routing answers a
  // missing config.json with index.html and a 200.
  if (!response.ok) {
    throw new AppConfigError(
      `The application configuration could not be loaded (HTTP ${response.status}).`,
    );
  }

  let body: unknown;
  try {
    body = await response.json();
  } catch (cause) {
    throw new AppConfigError('The application configuration is not valid JSON.', { cause });
  }

  assertAppConfig(body);

  return normalizeAppConfig(body);
}
