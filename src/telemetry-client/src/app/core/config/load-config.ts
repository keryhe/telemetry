import { AppConfig, DEFAULT_APP_CONFIG } from './app-config';

/**
 * Relative, so it resolves against `<base href>` and keeps working if the UI is ever mounted
 * under a sub-path. `fetch` resolves relative URLs against the document base URL, which the
 * `<base>` tag sets.
 */
const CONFIG_URL = 'config.json';

/** Past this, assume the config will never arrive and start with defaults rather than hang. */
const TIMEOUT_MS = 5_000;

/**
 * Fetches deployment configuration before the app bootstraps.
 *
 * Never rejects. Every failure path — absent file, timeout, malformed JSON, wrong shape — logs a
 * warning and returns {@link DEFAULT_APP_CONFIG}, because a UI that loads and talks to the
 * conventional same-origin `/api` is a far better outcome than a blank page. A genuinely
 * misconfigured deployment then fails visibly at the first API call, which is diagnosable.
 */
export async function loadAppConfig(): Promise<AppConfig> {
  try {
    const response = await fetch(CONFIG_URL, {
      cache: 'no-cache',
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });

    if (!response.ok) {
      return warnAndFallback(`responded ${response.status}`);
    }

    // A host using MapFallbackToFile("index.html") answers unknown paths with the SPA shell and a
    // 200, so an absent config.json arrives as HTML rather than as a 404. Without this check the
    // symptom would be an opaque JSON parse error at startup instead of a clear warning.
    const contentType = response.headers.get('content-type') ?? '';
    if (!contentType.includes('application/json')) {
      return warnAndFallback(`served '${contentType || 'no content-type'}' instead of JSON`);
    }

    const parsed: unknown = await response.json();
    if (parsed === null || typeof parsed !== 'object') {
      return warnAndFallback('did not contain a JSON object');
    }

    return { ...DEFAULT_APP_CONFIG, ...normalize(parsed as Partial<AppConfig>) };
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error);
    return warnAndFallback(`could not be loaded (${reason})`);
  }
}

/**
 * Drops keys that are present but unusable so they fall through to the defaults, and strips
 * trailing slashes from `apiUrl` — every call site appends `/traces`, `/logs` and so on, so a
 * configured `/api/` would otherwise produce `/api//traces`.
 */
function normalize(config: Partial<AppConfig>): Partial<AppConfig> {
  const normalized: Partial<AppConfig> = {};

  if (typeof config.apiUrl === 'string' && config.apiUrl.trim() !== '') {
    normalized.apiUrl = config.apiUrl.trim().replace(/\/+$/, '');
  } else if (config.apiUrl !== undefined) {
    console.warn(`[config] Ignoring invalid apiUrl; using '${DEFAULT_APP_CONFIG.apiUrl}'.`);
  }

  normalizeText(config, normalized, 'brandName');
  normalizeText(config, normalized, 'brandTagline');

  return normalized;
}

/** Shared trim-or-warn-and-fall-back handling for the two plain-text branding fields. */
function normalizeText<K extends 'brandName' | 'brandTagline'>(
  config: Partial<AppConfig>,
  normalized: Partial<AppConfig>,
  key: K,
): void {
  const value = config[key];
  if (typeof value === 'string' && value.trim() !== '') {
    normalized[key] = value.trim();
  } else if (value !== undefined) {
    console.warn(`[config] Ignoring invalid ${key}; using '${DEFAULT_APP_CONFIG[key]}'.`);
  }
}

function warnAndFallback(reason: string): AppConfig {
  console.warn(`[config] ${CONFIG_URL} ${reason}; using defaults.`, DEFAULT_APP_CONFIG);
  return DEFAULT_APP_CONFIG;
}
