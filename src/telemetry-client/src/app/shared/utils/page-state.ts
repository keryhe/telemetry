// Lightweight per-page state persistence (one localStorage key per page).
// State survives navigation and reload. Keys are namespaced, e.g. `state.metrics`.

/** Load a persisted page-state object, merged over the supplied defaults. */
export function loadPageState<T extends object>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    if (raw) return { ...fallback, ...(JSON.parse(raw) as Partial<T>) };
  } catch { /* corrupt or unavailable — fall back */ }
  return fallback;
}

/** Persist a page-state object. Failures (quota/private mode) are ignored. */
export function savePageState<T extends object>(key: string, value: T): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch { /* ignore */ }
}
