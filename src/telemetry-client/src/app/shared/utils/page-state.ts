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

/**
 * Page-state fields that name something belonging to one tenant (a service, an operation, a
 * search, metric labels). Reset on a tenant switch so a filter chosen under one tenant does not
 * silently empty the next tenant's view. A page that persists a new tenant-specific filter must be
 * added here.
 */
const TENANT_SCOPED_PAGE_STATE: Record<string, Record<string, unknown>> = {
  'state.traces': { selectedService: '', selectedOperation: '', searchText: '' },
  'state.logs': { selectedService: '', searchText: '' },
  'state.metrics': { selectedService: '', searchText: '' },
  'state.dashboard': { selectedService: '' },
  'state.metricDetail': { selectedService: '', selectedLabels: {} },
};

/** Clear the tenant-specific fields of every persisted page state, leaving the rest intact. */
export function resetTenantScopedPageState(): void {
  for (const [key, reset] of Object.entries(TENANT_SCOPED_PAGE_STATE)) {
    try {
      if (localStorage.getItem(key) === null) continue;
    } catch { continue; }
    savePageState(key, { ...loadPageState<Record<string, unknown>>(key, {}), ...reset });
  }
}
