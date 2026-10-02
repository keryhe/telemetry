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
  const o = overrides as Record<string, unknown>;

  const rate = (v: unknown) => isNumber(v) && v > 0 && v <= 1;
  const positive = (v: unknown) => isNumber(v) && v > 0;

  const band = (
    name: 'errorRate' | 'p95Ms' | 'logErrorRate', valid: (v: unknown) => boolean,
  ): { warn: number; critical: number } => {
    const raw = o[name];
    const merged = { ...base[name] };
    if (raw === undefined) return merged;
    if (raw === null || typeof raw !== 'object') {
      console.warn(`[config] Ignoring invalid healthThresholds.${name}; using defaults.`);
      return merged;
    }
    for (const key of ['warn', 'critical'] as const) {
      const value = (raw as Record<string, unknown>)[key];
      if (value === undefined) continue;
      if (valid(value)) merged[key] = value as number;
      else console.warn(`[config] Ignoring invalid healthThresholds.${name}.${key}; using ${base[name][key]}.`);
    }
    if (merged.warn >= merged.critical) {
      console.warn(`[config] healthThresholds.${name}: warn must be below critical; using defaults.`);
      return { ...base[name] };
    }
    return merged;
  };

  const scalar = (name: 'minSamples' | 'tailRatio' | 'silentAfterMs', valid: (v: unknown) => boolean): number => {
    const value = o[name];
    if (value === undefined) return base[name];
    if (valid(value)) return value as number;
    console.warn(`[config] Ignoring invalid healthThresholds.${name}; using ${base[name]}.`);
    return base[name];
  };

  return {
    errorRate: band('errorRate', rate),
    p95Ms: band('p95Ms', positive),
    logErrorRate: band('logErrorRate', rate),
    minSamples: scalar('minSamples', (v) => isNumber(v) && Number.isInteger(v) && v >= 1),
    tailRatio: scalar('tailRatio', (v) => isNumber(v) && v > 1),
    silentAfterMs: scalar('silentAfterMs', positive),
  };
}

function isNumber(v: unknown): v is number {
  return typeof v === 'number' && Number.isFinite(v);
}

/**
 * Injected rather than imported directly so a deployment can override the numbers without touching
 * either dashboard: `main.ts` provides the defaults merged with the host's `config.json`
 * overrides ({@link resolveHealthThresholds}). The `factory` default means providing it is
 * optional — anything injecting the token works with no provider registered.
 */
export const HEALTH_THRESHOLDS_TOKEN = new InjectionToken<HealthThresholds>(
  'health.thresholds',
  { providedIn: 'root', factory: () => HEALTH_THRESHOLDS },
);

/** Card / stat colouring, matching `app-stat-card`'s `color` input. */
export type HealthColor = 'default' | 'warn' | 'error';

/** A tenant's overall state, worst-first in the order the Global Dashboard sorts by. */
export type TenantStatus = 'unknown' | 'degraded' | 'warning' | 'slow tail' | 'silent' | 'healthy';

/** Sort weight for `TenantStatus` — lower sorts first, so problems lead the grid. */
export const TENANT_STATUS_ORDER: Record<TenantStatus, number> = {
  'unknown': 0,
  'degraded': 1,
  'warning': 2,
  'slow tail': 3,
  'silent': 4,
  'healthy': 5,
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
  logCount: number;
  logErrorCount: number;
  lastSeenUtc: Date | null;
  failed: boolean;
}

/** What a matched condition measured. The legend and the card both format from these. */
export type HealthReasonKind =
  | 'failed' | 'silent' | 'errorRate' | 'p95' | 'logErrors' | 'slowTail';

/**
 * One condition that matched, as data rather than text, so the card's reason line and the legend
 * format the same numbers the same way. `severity` is the status the condition alone would give.
 */
export interface HealthReason {
  kind: HealthReasonKind;
  severity: Exclude<TenantStatus, 'healthy'>;
  /** The measured value: a 0–1 fraction, milliseconds, or a ratio (see `kind`). Absent for `failed`. */
  value?: number;
  /** The threshold that was crossed. Absent for `failed`. */
  threshold?: number;
}

export interface TenantHealth {
  status: TenantStatus;
  /** Every matched condition, worst first. Empty for `healthy`. The first is the primary reason. */
  reasons: HealthReason[];
}

/**
 * Evaluates every condition, then takes the status from the worst one; the rest stay in `reasons`
 * so a card can show what else is wrong. Severity order is {@link TENANT_STATUS_ORDER}:
 * a tenant that is both erroring and slow reads as `degraded`, and `warning` outranks `slow tail`.
 *
 * An empty window is not by itself silence — a tenant seen seconds ago that simply has no
 * traffic in this particular window is healthy, not down. Silence is only a question when the
 * window itself is empty: testing last-seen first would label a tenant "silent" while its own
 * card showed traffic and errors, which is what happens whenever the selected window is
 * historical, since last-seen is measured against now.
 */
export function tenantHealth(s: TenantHealthInput, t: HealthThresholds): TenantHealth {
  if (s.failed) {
    return { status: 'unknown', reasons: [{ kind: 'failed', severity: 'unknown' }] };
  }

  if (s.traceCount === 0) {
    const staleFor = s.lastSeenUtc ? Date.now() - s.lastSeenUtc.getTime() : Infinity;
    if (staleFor > t.silentAfterMs) {
      return {
        status: 'silent',
        reasons: [{ kind: 'silent', severity: 'silent', value: staleFor, threshold: t.silentAfterMs }],
      };
    }
    return { status: 'healthy', reasons: [] };
  }

  const reasons: HealthReason[] = [];
  const errorRate = s.errorCount / s.traceCount;
  const errorColor = classifyErrorRate(errorRate, t);
  if (errorColor === 'error') {
    reasons.push({ kind: 'errorRate', severity: 'degraded', value: errorRate, threshold: t.errorRate.critical });
  } else if (errorColor === 'warn') {
    reasons.push({ kind: 'errorRate', severity: 'warning', value: errorRate, threshold: t.errorRate.warn });
  }

  if (hasEnoughSamples(s.traceCount, t)) {
    const p95Color = classifyP95(s.p95Ms, s.traceCount, t);
    if (p95Color === 'error') {
      reasons.push({ kind: 'p95', severity: 'degraded', value: s.p95Ms, threshold: t.p95Ms.critical });
    } else if (p95Color === 'warn') {
      reasons.push({ kind: 'p95', severity: 'warning', value: s.p95Ms, threshold: t.p95Ms.warn });
    }
  }

  if (s.logCount > 0) {
    const logRate = s.logErrorCount / s.logCount;
    // Only the critical band raises status; the warn band stays a colour on the card.
    if (classifyLogErrorRate(s.logErrorCount, s.logCount, t) === 'error') {
      reasons.push({ kind: 'logErrors', severity: 'warning', value: logRate, threshold: t.logErrorRate.critical });
    }
  }

  if (hasEnoughSamples(s.traceCount, t) && hasSlowTail(s.p95Ms, s.p99Ms, t)) {
    reasons.push({ kind: 'slowTail', severity: 'slow tail', value: s.p99Ms / s.p95Ms, threshold: t.tailRatio });
  }

  if (reasons.length === 0) return { status: 'healthy', reasons };

  // Stable sort: equal severities keep the order they were pushed in (error rate, p95, logs, tail).
  reasons.sort((a, b) => TENANT_STATUS_ORDER[a.severity] - TENANT_STATUS_ORDER[b.severity]);
  return { status: reasons[0].severity, reasons };
}

/** The status word alone, for callers that do not need the reasons. */
export function tenantStatus(s: TenantHealthInput, t: HealthThresholds): TenantStatus {
  return tenantHealth(s, t).status;
}
