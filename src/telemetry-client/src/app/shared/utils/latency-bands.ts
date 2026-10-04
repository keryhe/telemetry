/**
 * The request rollup's 24 doubling duration bands (plans/summary-rollups.md): band 0 is under 0.25 ms, band N (1-22) is
 * [0.25 ms * 2^(N-1), 0.25 ms * 2^N), and band 23 is 1,048.6 s and over. Mirrors the server's `DurationBands`.
 */
export const BAND_COUNT = 24;
const FIRST_EDGE_NANOS = 250_000;

/** Lower edge of a band in nanoseconds (0 for band 0). */
export function bandLowerNanos(band: number): number {
  return band <= 0 ? 0 : FIRST_EDGE_NANOS * 2 ** (band - 1);
}

/** Exclusive upper edge of a band in nanoseconds; null for the open last band. */
export function bandUpperNanos(band: number): number | null {
  return band >= BAND_COUNT - 1 ? null : FIRST_EDGE_NANOS * 2 ** band;
}

/** What a click on a latency bubble sets on the trace list (decision 12). */
export interface BubbleTarget {
  start: Date;
  end: Date;
  /** Always set: band 0 sends an explicit 0, since slow mode would otherwise default a missing minimum to 500 ms. */
  minDurationMs: number;
  /** Null for the open last band (no maximum). */
  maxDurationMs: number | null;
}

/**
 * A bubble click zooms to the bubble's time span and filters the list to Slow mode with the band as its duration range.
 * The bands exclude their upper edge and the list's maximum includes it, so the maximum is the upper edge minus 1 ns.
 * Slow mode is limited to `rawSearchWindowHours` (the server rejects a wider window), so a wider bubble sets the newest
 * `rawSearchWindowHours` of itself instead and the click never gets a 400.
 */
export function bubbleTarget(
  cell: { xStart: Date; xEnd: Date; band: number },
  rawSearchWindowHours: number | null,
): BubbleTarget {
  let start = cell.xStart;
  const end = cell.xEnd;
  if (rawSearchWindowHours != null) {
    const limitMs = rawSearchWindowHours * 3_600_000;
    if (end.getTime() - start.getTime() > limitMs) start = new Date(end.getTime() - limitMs);
  }
  const upper = bandUpperNanos(cell.band);
  return {
    start,
    end,
    minDurationMs: bandLowerNanos(cell.band) / 1e6,
    maxDurationMs: upper == null ? null : (upper - 1) / 1e6,
  };
}
