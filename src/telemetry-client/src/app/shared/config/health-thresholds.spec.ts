import {
  HEALTH_THRESHOLDS, TenantHealthInput, resolveHealthThresholds, tenantHealth,
} from './health-thresholds';

const t = HEALTH_THRESHOLDS;

/** A busy, healthy tenant; each test overrides only what it is about. */
function input(overrides: Partial<TenantHealthInput> = {}): TenantHealthInput {
  return {
    traceCount: 1000,
    errorCount: 0,
    p95Ms: 100,
    p99Ms: 150,
    logCount: 1000,
    logErrorCount: 0,
    lastSeenUtc: new Date(),
    failed: false,
    ...overrides,
  };
}

describe('tenantHealth', () => {
  it('is healthy with no reasons when nothing crosses a band', () => {
    expect(tenantHealth(input(), t)).toEqual({ status: 'healthy', reasons: [] });
  });

  it('is unknown when the stats request failed', () => {
    const h = tenantHealth(input({ failed: true }), t);
    expect(h.status).toBe('unknown');
    expect(h.reasons[0].kind).toBe('failed');
  });

  describe('empty window', () => {
    it('is silent when nothing was seen for longer than silentAfterMs', () => {
      const lastSeenUtc = new Date(Date.now() - t.silentAfterMs - 1000);
      expect(tenantHealth(input({ traceCount: 0, lastSeenUtc }), t).status).toBe('silent');
    });

    it('is silent when the tenant has never been seen', () => {
      expect(tenantHealth(input({ traceCount: 0, lastSeenUtc: null }), t).status).toBe('silent');
    });

    it('is healthy (idle, not down) when seen recently', () => {
      const lastSeenUtc = new Date(Date.now() - 1000);
      expect(tenantHealth(input({ traceCount: 0, lastSeenUtc }), t).status).toBe('healthy');
    });

    it('does not call a tenant with traffic silent however stale last-seen is', () => {
      const lastSeenUtc = new Date(Date.now() - 24 * 3600 * 1000);
      expect(tenantHealth(input({ lastSeenUtc }), t).status).toBe('healthy');
    });
  });

  describe('error rate', () => {
    it('is warning from the warn band', () => {
      const h = tenantHealth(input({ errorCount: 20 }), t); // 2%
      expect(h.status).toBe('warning');
      expect(h.reasons[0]).toEqual(jasmine.objectContaining({ kind: 'errorRate', threshold: t.errorRate.warn }));
    });

    it('is degraded from the critical band', () => {
      const h = tenantHealth(input({ errorCount: 60 }), t); // 6%
      expect(h.status).toBe('degraded');
      expect(h.reasons[0]).toEqual(jasmine.objectContaining({ kind: 'errorRate', threshold: t.errorRate.critical }));
    });

    it('applies with a small sample, unlike the latency checks', () => {
      expect(tenantHealth(input({ traceCount: 10, errorCount: 1 }), t).status).toBe('degraded');
    });
  });

  describe('p95', () => {
    it('is warning from the warn band', () => {
      expect(tenantHealth(input({ p95Ms: 800, p99Ms: 900 }), t).status).toBe('warning');
    });

    it('is degraded from the critical band', () => {
      expect(tenantHealth(input({ p95Ms: 2000, p99Ms: 2500 }), t).status).toBe('degraded');
    });

    it('is ignored below minSamples', () => {
      const h = tenantHealth(input({ traceCount: t.minSamples - 1, p95Ms: 5000, p99Ms: 6000 }), t);
      expect(h.status).toBe('healthy');
    });
  });

  describe('log errors', () => {
    it('raises warning only at the critical band', () => {
      expect(tenantHealth(input({ logErrorCount: 250 }), t).status).toBe('warning'); // 25%
      expect(tenantHealth(input({ logErrorCount: 100 }), t).status).toBe('healthy'); // 10%: warn band, colour only
    });

    it('is ignored when there are no logs', () => {
      expect(tenantHealth(input({ logCount: 0, logErrorCount: 0 }), t).status).toBe('healthy');
    });
  });

  describe('slow tail', () => {
    it('is flagged when p99 is more than tailRatio times p95', () => {
      const h = tenantHealth(input({ p95Ms: 100, p99Ms: 300 }), t);
      expect(h.status).toBe('slow tail');
      expect(h.reasons[0].kind).toBe('slowTail');
    });

    it('is ignored below minSamples', () => {
      expect(tenantHealth(input({ traceCount: 50, p95Ms: 100, p99Ms: 300 }), t).status).toBe('healthy');
    });
  });

  describe('precedence', () => {
    it('lets warning outrank slow tail and keeps the tail as a secondary reason', () => {
      const h = tenantHealth(input({ errorCount: 30, p95Ms: 100, p99Ms: 300 }), t); // 3% + tail
      expect(h.status).toBe('warning');
      expect(h.reasons.map((r) => r.kind)).toEqual(['errorRate', 'slowTail']);
    });

    it('lets degraded outrank warning and lists every match, worst first', () => {
      const h = tenantHealth(input({ errorCount: 60, p95Ms: 800, p99Ms: 900, logErrorCount: 300 }), t);
      expect(h.status).toBe('degraded');
      expect(h.reasons.map((r) => r.kind)).toEqual(['errorRate', 'p95', 'logErrors']);
    });
  });
});

describe('resolveHealthThresholds', () => {
  beforeEach(() => spyOn(console, 'warn'));

  it('returns the defaults when there are no overrides', () => {
    expect(resolveHealthThresholds(undefined, t)).toBe(t);
    expect(resolveHealthThresholds(null, t)).toBe(t);
  });

  it('merges a partial override field by field', () => {
    const r = resolveHealthThresholds({ errorRate: { critical: 0.1 }, minSamples: 50 }, t);
    expect(r.errorRate).toEqual({ warn: t.errorRate.warn, critical: 0.1 });
    expect(r.minSamples).toBe(50);
    expect(r.p95Ms).toEqual(t.p95Ms);
    expect(r.silentAfterMs).toBe(t.silentAfterMs);
  });

  it('drops an unusable value with a warning and keeps the default', () => {
    const r = resolveHealthThresholds({ errorRate: { warn: 'lots' }, tailRatio: 0.5, minSamples: 2.5 }, t);
    expect(r.errorRate).toEqual(t.errorRate);
    expect(r.tailRatio).toBe(t.tailRatio);
    expect(r.minSamples).toBe(t.minSamples);
    expect(console.warn).toHaveBeenCalled();
  });

  it('drops a pair whose warn is not below its critical', () => {
    const r = resolveHealthThresholds({ p95Ms: { warn: 2000 } }, t); // above the default critical 1500
    expect(r.p95Ms).toEqual(t.p95Ms);
  });

  it('rejects a rate above 1', () => {
    expect(resolveHealthThresholds({ errorRate: { critical: 5 } }, t).errorRate).toEqual(t.errorRate);
  });

  it('does not mutate the defaults', () => {
    const before = JSON.stringify(t);
    resolveHealthThresholds({ errorRate: { critical: 0.2 } }, t);
    expect(JSON.stringify(t)).toBe(before);
  });
});
