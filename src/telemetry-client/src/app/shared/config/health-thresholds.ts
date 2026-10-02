import { InjectionToken } from '@angular/core';

/**
 * The thresholds that decide whether something "looks off at a glance" on the Dashboard, plus the
 * classifier that applies them.
 *
 * Distinct from alert rules, which are per-tenant, per-rule-type and often scoped to a single
 * service. Alerts own "is this a violation"; these own "does this look wrong on a dashboard".
 */
export interface HealthThresholds {
  /** Error rate as a 0–1 fraction (NOT the 0–100 percentage the API's ServiceStats uses). */
  errorRate: { warn: number; critical: number };
}

export const HEALTH_THRESHOLDS: HealthThresholds = {
  errorRate: { warn: 0.01, critical: 0.05 },
};

/**
 * Merges a deployment's overrides (from `config.json`) over `base`, field by field.
 *
 * Never throws: a value that is not a usable number is dropped with a warning and the default
 * stays, because a page that loads with default thresholds is a better outcome than a blank one.
 * The server validates the same rules at startup, so this is the backstop for a hand-edited
 * `config.json` or an older host. A pair whose `warn` is not below its `critical` is dropped as a
 * pair, since the classifier assumes that ordering.
 */
export function resolveHealthThresholds(overrides: unknown, base: HealthThresholds): HealthThresholds {
  if (overrides === null || typeof overrides !== 'object') return base;
  const raw = (overrides as Record<string, unknown>)['errorRate'];
  const merged = { ...base.errorRate };
  if (raw === undefined) return { errorRate: merged };
  if (raw === null || typeof raw !== 'object') {
    console.warn('[config] Ignoring invalid healthThresholds.errorRate; using defaults.');
    return { errorRate: merged };
  }

  for (const key of ['warn', 'critical'] as const) {
    const value = (raw as Record<string, unknown>)[key];
    if (value === undefined) continue;
    if (typeof value === 'number' && Number.isFinite(value) && value > 0 && value <= 1) {
      merged[key] = value;
    } else {
      console.warn(`[config] Ignoring invalid healthThresholds.errorRate.${key}; using ${base.errorRate[key]}.`);
    }
  }
  if (merged.warn >= merged.critical) {
    console.warn('[config] healthThresholds.errorRate: warn must be below critical; using defaults.');
    return { errorRate: { ...base.errorRate } };
  }
  return { errorRate: merged };
}

/**
 * Injected rather than imported directly so a deployment can override the numbers without touching
 * the Dashboard: `main.ts` provides the defaults merged with the host's `config.json`
 * overrides ({@link resolveHealthThresholds}). The `factory` default means providing it is
 * optional — anything injecting the token works with no provider registered.
 */
export const HEALTH_THRESHOLDS_TOKEN = new InjectionToken<HealthThresholds>(
  'health.thresholds',
  { providedIn: 'root', factory: () => HEALTH_THRESHOLDS },
);

/** Card / stat colouring, matching `app-stat-card`'s `color` input. */
export type HealthColor = 'default' | 'warn' | 'error';

/** `rate` is a 0–1 fraction. */
export function classifyErrorRate(rate: number, t: HealthThresholds): HealthColor {
  if (rate >= t.errorRate.critical) return 'error';
  if (rate >= t.errorRate.warn) return 'warn';
  return 'default';
}
