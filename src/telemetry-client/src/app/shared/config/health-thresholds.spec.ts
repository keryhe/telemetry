import { HEALTH_THRESHOLDS, classifyErrorRate, resolveHealthThresholds } from './health-thresholds';

const t = HEALTH_THRESHOLDS;

describe('classifyErrorRate', () => {
  it('is default below the warn band', () => {
    expect(classifyErrorRate(0.005, t)).toBe('default');
  });

  it('is warn from the warn band', () => {
    expect(classifyErrorRate(t.errorRate.warn, t)).toBe('warn');
  });

  it('is error from the critical band', () => {
    expect(classifyErrorRate(t.errorRate.critical, t)).toBe('error');
  });
});

describe('resolveHealthThresholds', () => {
  beforeEach(() => spyOn(console, 'warn'));

  it('returns the defaults when there are no overrides', () => {
    expect(resolveHealthThresholds(undefined, t)).toBe(t);
    expect(resolveHealthThresholds(null, t)).toBe(t);
  });

  it('merges a partial override field by field', () => {
    const r = resolveHealthThresholds({ errorRate: { critical: 0.1 } }, t);
    expect(r.errorRate).toEqual({ warn: t.errorRate.warn, critical: 0.1 });
  });

  it('drops an unusable value with a warning and keeps the default', () => {
    const r = resolveHealthThresholds({ errorRate: { warn: 'lots' } }, t);
    expect(r.errorRate).toEqual(t.errorRate);
    expect(console.warn).toHaveBeenCalled();
  });

  it('drops a pair whose warn is not below its critical', () => {
    const r = resolveHealthThresholds({ errorRate: { warn: 0.5 } }, t); // above the default critical
    expect(r.errorRate).toEqual(t.errorRate);
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
