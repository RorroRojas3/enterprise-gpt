import { AppConfig, AppConfigError, assertAppConfig, normalizeAppConfig } from './app-config';

/** How long to wait for `config.json` before giving up and showing the fatal shell. */
const FETCH_TIMEOUT_MS = 10_000;

/**
 * Fetches and validates `config.json`.
 *
 * @returns The frozen, normalized configuration.
 * @throws {AppConfigError} When the file is unreachable, not JSON, or invalid.
 */
export async function loadAppConfig(): Promise<AppConfig> {
  // Resolved against document.baseURI rather than '/config.json' so a sub-path
  // deployment works, and rather than './config.json' so a hard reload on a deep
  // route such as /chat/{id} still resolves to the application root.
  const url = new URL('config.json', document.baseURI);

  let response: Response;
  try {
    response = await fetch(url, {
      // A cached config is how one environment ends up pointed at another.
      cache: 'no-store',
      // config.json is public deployment metadata; never attach credentials, which
      // would leak cookies to a CDN origin.
      credentials: 'omit',
      // Without this a wedged host produces the blank page the fatal shell exists
      // to prevent.
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
