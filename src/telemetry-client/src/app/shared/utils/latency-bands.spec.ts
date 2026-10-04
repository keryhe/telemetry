import { BAND_COUNT, bandLowerNanos, bandUpperNanos, bubbleTarget } from './latency-bands';

describe('latency bands', () => {
  const cell = (band: number, spanMinutes = 5) => {
    const xStart = new Date('2026-10-01T10:00:00Z');
    return { xStart, xEnd: new Date(xStart.getTime() + spanMinutes * 60_000), band };
  };

  it('has the server band edges', () => {
    expect(bandLowerNanos(0)).toBe(0);
    expect(bandUpperNanos(0)).toBe(250_000);
    expect(bandLowerNanos(1)).toBe(250_000);
    expect(bandUpperNanos(1)).toBe(500_000);
    expect(bandLowerNanos(BAND_COUNT - 1)).toBe(1_048_576_000_000);
    expect(bandUpperNanos(BAND_COUNT - 1)).toBeNull();
    for (let band = 1; band < BAND_COUNT - 1; band++)
      expect(bandUpperNanos(band)).toBe(bandLowerNanos(band + 1));
  });

  it('sets the band minus 1 ns as the maximum and the lower edge as the minimum', () => {
    const t = bubbleTarget(cell(4), null);          // [2 ms, 4 ms)
    expect(t.minDurationMs).toBe(2);
    expect(t.maxDurationMs).toBeCloseTo(3.999999, 9);
  });

  it('sends an explicit 0 minimum for the first band', () => {
    const t = bubbleTarget(cell(0), null);
    expect(t.minDurationMs).toBe(0);
    expect(t.maxDurationMs).toBeCloseTo(0.249999, 9);
  });

  it('sends no maximum for the open last band', () => {
    const t = bubbleTarget(cell(BAND_COUNT - 1), null);
    expect(t.minDurationMs).toBe(1_048_576);
    expect(t.maxDurationMs).toBeNull();
  });

  it('zooms to the bubble span when it fits the slow-mode window', () => {
    const c = cell(5, 60);
    const t = bubbleTarget(c, 24);
    expect(t.start).toEqual(c.xStart);
    expect(t.end).toEqual(c.xEnd);
  });

  it('keeps the newest slow-mode window of a bubble wider than it', () => {
    const c = cell(5, 36 * 60);                       // a 36 h bubble
    const t = bubbleTarget(c, 24);
    expect(t.end).toEqual(c.xEnd);
    expect(t.end.getTime() - t.start.getTime()).toBe(24 * 3_600_000);
    // Honors an operator-lowered limit too.
    const lowered = bubbleTarget(cell(5, 120), 1);
    expect(lowered.end.getTime() - lowered.start.getTime()).toBe(3_600_000);
  });
});
