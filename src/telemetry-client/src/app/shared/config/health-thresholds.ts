import { InjectionToken } from '@angular/core';

/**
 * The thresholds that decide whether a tenant or a service "looks off at a glance", plus the
 * classifiers that apply them.
 *
 * Deliberately one shared module rather than per-page constants: the Global Dashboard renders a
 * colour legend, and a page that coloured its cards from its own copy of these numbers would
 * eventually disagree with the legend explaining them. Both dashboards inject the same token, so
 * the legend and the colouring cannot drift.
 *
 * Distinct from alert rules, which are per-tenant, per-rule-type and often scoped to a single
 * service. Alerts own "is this a violation"; these own "does this look wrong on a dashboard".
 */
export interface HealthThresholds {
  /** Error rate as a 0–1 fraction (NOT the 0–100 percentage the API's ServiceStats uses). */
  errorRate: { warn: number; critical: number };

  /** Window p95 trace duration, milliseconds. */
  p95Ms: { warn: number; critical: number };

  /**
   * Error+Fatal log records as a 0–1 fraction of all log records in the window.
   *
   * Deliberately looser than {@link errorRate}: a logged error is not a failed request. Plenty
   * of healthy services log errors routinely — a retry that later succeeded, a rejected input,
   * a noisy dependency — so trace-level bands applied here would paint every card red. These
   * are a starting guess and want tuning against real traffic.
   */
  logErrorRate: { warn: number; critical: number };

  /**
   * Minimum traces in the window before a percentile is worth showing. Below this the p95 is
   * effectively "the slowest request" — nearest-rank makes that literal — so it is muted rather
   * than shown as a jittery number that would train people to distrust the card.
   */
  minSamples: number;

  /**
   * p99/p95 above this means a tail the p95 alone is hiding, and the card surfaces the p99.
   * A guess until it has been tuned against real data: too low and every card grows a chip.
   */
  tailRatio: number;

  /** No telemetry for longer than this (ms) and a tenant reads as silent rather than idle. */
  silentAfterMs: number;
}

export const HEALTH_THRESHOLDS: HealthThresholds = {
  // Carried over unchanged from the per-tenant dashboard, which hardcoded these two.
  errorRate: { warn: 0.01, critical: 0.05 },
  p95Ms: { warn: 500, critical: 1500 },
  logErrorRate: { warn: 0.05, critical: 0.20 },
  minSamples: 100,
  tailRatio: 2,
  silentAfterMs: 5 * 60 * 1000,
};

/**
 * Injected rather than imported directly so a deployment can override the numbers in one place
 * (`app.config.ts`) without touching either dashboard. The `factory` default means providing it
 * is optional — anything injecting the token works with no provider registered.
 */
export const HEALTH_THRESHOLDS_TOKEN = new InjectionToken<HealthThresholds>(
  'health.thresholds',
  { providedIn: 'root', factory: () => HEALTH_THRESHOLDS },
);

/** Card / stat colouring, matching `app-stat-card`'s `color` input. */
export type HealthColor = 'default' | 'warn' | 'error';

/** A tenant's overall state, worst-first in the order the Global Dashboard sorts by. */
export type TenantStatus = 'unknown' | 'degraded' | 'slow tail' | 'silent' | 'healthy';

/** Sort weight for `TenantStatus` — lower sorts first, so problems lead the grid. */
export const TENANT_STATUS_ORDER: Record<TenantStatus, number> = {
  'unknown': 0,
  'degraded': 1,
  'slow tail': 2,
  'silent': 3,
  'healthy': 4,
};

/** `rate` is a 0–1 fraction. */
export function classifyErrorRate(rate: number, t: HealthThresholds): HealthColor {
  if (rate >= t.errorRate.critical) return 'error';
  if (rate >= t.errorRate.warn) return 'warn';
  return 'default';
}

/**
 * Colour for a window p95. Returns `'default'` below `minSamples` as well as when the latency is
 * fine — the caller is expected to check `hasEnoughSamples` and render the value itself as
 * unavailable, rather than colour a number it is not going to show.
 */
export function classifyP95(ms: number, sampleCount: number, t: HealthThresholds): HealthColor {
  if (!hasEnoughSamples(sampleCount, t)) return 'default';
  if (ms >= t.p95Ms.critical) return 'error';
  if (ms >= t.p95Ms.warn) return 'warn';
  return 'default';
}

/**
 * Colour for the Error+Fatal share of a window's logs. Separate from
 * {@link classifyErrorRate} because it reads different bands, not because the arithmetic
 * differs — see {@link HealthThresholds.logErrorRate}.
 */
export function classifyLogErrorRate(
  errorCount: number, totalCount: number, t: HealthThresholds,
): HealthColor {
  if (totalCount <= 0) return 'default';
  const rate = errorCount / totalCount;
  if (rate >= t.logErrorRate.critical) return 'error';
  if (rate >= t.logErrorRate.warn) return 'warn';
  return 'default';
}

export function hasEnoughSamples(sampleCount: number, t: HealthThresholds): boolean {
  return sampleCount >= t.minSamples;
}

/** True when the tail runs far enough past the p95 that the p95 alone understates it. */
export function hasSlowTail(p95Ms: number, p99Ms: number, t: HealthThresholds): boolean {
  return p95Ms > 0 && p99Ms / p95Ms > t.tailRatio;
}

/** Input for {@link tenantStatus} — the subset of a tenant card's stats that decides its state. */
export interface TenantHealthInput {
  traceCount: number;
  errorCount: number;
  p95Ms: number;
  p99Ms: number;
  lastSeenUtc: Date | null;
  failed: boolean;
}

/**
 * The single state word a card leads with. Ordered by severity: a tenant that is both erroring
 * and slow reads as `degraded`, because that is the more actionable of the two.
 *
 * An empty window is not by itself silence — a tenant seen seconds ago that simply has no
 * traffic in this particular window is healthy, not down.
 */
export function tenantStatus(s: TenantHealthInput, t: HealthThresholds): TenantStatus {
  if (s.failed) return 'unknown';

  // Silence is only a question when the window itself is empty. Testing last-seen first would
  // label a tenant "silent" while its own card showed traffic and errors — which is what happens
  // whenever the selected window is historical, since last-seen is measured against now.
  if (s.traceCount === 0) {
    const staleFor = s.lastSeenUtc ? Date.now() - s.lastSeenUtc.getTime() : Infinity;
    return staleFor > t.silentAfterMs ? 'silent' : 'healthy';
  }

  const errorRate = s.errorCount / s.traceCount;
  if (classifyErrorRate(errorRate, t) === 'error') return 'degraded';
  if (hasEnoughSamples(s.traceCount, t) && classifyP95(s.p95Ms, s.traceCount, t) === 'error') return 'degraded';
  if (hasEnoughSamples(s.traceCount, t) && hasSlowTail(s.p95Ms, s.p99Ms, t)) return 'slow tail';
  if (classifyErrorRate(errorRate, t) === 'warn') return 'degraded';

  return 'healthy';
}
