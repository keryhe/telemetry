import { inject, Injectable, signal } from '@angular/core';
import { Params } from '@angular/router';
import { UrlStateService } from '../../shared/utils/url-state';

export type TimePreset = '1h' | '6h' | '24h' | '7d' | '30d';

export interface TimeRange {
  preset: TimePreset | 'custom';
  start: Date;
  end: Date;
}

const PRESET_DURATIONS: Record<TimePreset, number> = {
  '1h':  60 * 60 * 1000,
  '6h':  6 * 60 * 60 * 1000,
  '24h': 24 * 60 * 60 * 1000,
  '7d':  7 * 24 * 60 * 60 * 1000,
  '30d': 30 * 24 * 60 * 60 * 1000,
};

const PRESET_LABELS: Record<TimePreset, string> = {
  '1h':  'Last 1 Hour',
  '6h':  'Last 6 Hours',
  '24h': 'Last 24 Hours',
  '7d':  'Last 7 Days',
  '30d': 'Last 30 Days',
};

/** Time-range-aware auto-refresh cadence, matching Blazor's GetRecommendedRefreshInterval(). */
const REFRESH_INTERVALS: Record<TimePreset, number> = {
  '1h':  30 * 1000,
  '6h':  60 * 1000,
  '24h': 5 * 60 * 1000,
  '7d':  15 * 60 * 1000,
  '30d': 30 * 60 * 1000,
};

export function recommendedRefreshIntervalMs(preset: TimePreset): number {
  return REFRESH_INTERVALS[preset];
}

const STORAGE_KEY = 'state.timeRange';

interface StoredTimeRange {
  preset: TimePreset | 'custom';
  start: string;
  end: string;
}

const PRESETS: TimePreset[] = ['1h', '6h', '24h', '7d', '30d'];

@Injectable({ providedIn: 'root' })
export class TimeRangeService {
  private readonly urlState = inject(UrlStateService);

  readonly range = signal<TimeRange>(this.loadInitial());

  /** Guard: true while we're applying a range that came from the URL, so we don't write it back. */
  private applyingFromUrl = false;
  /** On the first reconcile we only adopt params (never write) to avoid racing initial navigation. */
  private firstReconcile = true;

  constructor() {
    // Keep the URL in sync app-wide: URL params win on load and on back/forward; otherwise the
    // current range is pushed into the URL so every view is shareable/deep-linkable.
    this.urlState.changes().subscribe((params) => this.reconcileFromUrl(params));
  }

  setPreset(preset: TimePreset): void {
    const range = this.fromPreset(preset);
    this.range.set(range);
    this.persist(range);
    this.writeToUrl(range);
  }

  setCustom(start: Date, end: Date): void {
    const range: TimeRange = { preset: 'custom', start, end };
    this.range.set(range);
    this.persist(range);
    this.writeToUrl(range);
  }

  /** Adopt time params from the URL (URL wins), or seed the URL from the current range when absent. */
  private reconcileFromUrl(params: Params): void {
    const isFirst = this.firstReconcile;
    this.firstReconcile = false;

    const rangeParam = params['range'] as string | undefined;
    const from = params['from'] as string | undefined;
    const to = params['to'] as string | undefined;
    const current = this.range();

    if (rangeParam && (PRESETS as string[]).includes(rangeParam)) {
      if (current.preset !== rangeParam) this.applyFromUrl(() => this.setPreset(rangeParam as TimePreset));
      return;
    }
    if (from && to) {
      const start = new Date(from);
      const end = new Date(to);
      const valid = !isNaN(start.getTime()) && !isNaN(end.getTime());
      const differs = current.preset !== 'custom' ||
        current.start.getTime() !== start.getTime() || current.end.getTime() !== end.getTime();
      if (valid && differs) this.applyFromUrl(() => this.setCustom(start, end));
      return;
    }
    // No time params in the URL yet — reflect the current range so the link carries state.
    // Skip on the very first reconcile so we don't navigate while initial routing is in flight.
    if (!isFirst) this.writeToUrl(current);
  }

  private applyFromUrl(action: () => void): void {
    this.applyingFromUrl = true;
    try { action(); } finally { this.applyingFromUrl = false; }
  }

  private writeToUrl(range: TimeRange): void {
    if (this.applyingFromUrl) return;
    if (range.preset === 'custom') {
      this.urlState.patch({ range: null, from: range.start.toISOString(), to: range.end.toISOString() });
    } else {
      this.urlState.patch({ range: range.preset, from: null, to: null });
    }
  }

  /**
   * Re-resolve the current window to "now" for relative presets, so navigating
   * back to a page slides the window forward. Custom absolute ranges are left as-is.
   */
  refreshRelativeWindow(): void {
    const current = this.range();
    if (current.preset !== 'custom') {
      this.setPreset(current.preset);
    }
  }

  /** Display label for a preset (matches Blazor wording). */
  presetLabel(preset: TimePreset): string {
    return PRESET_LABELS[preset];
  }

  private fromPreset(preset: TimePreset): TimeRange {
    const end = new Date();
    const start = new Date(end.getTime() - PRESET_DURATIONS[preset]);
    return { preset, start, end };
  }

  private loadInitial(): TimeRange {
    const stored = this.readStored();
    if (!stored) return this.fromPreset('1h');

    if (stored.preset === 'custom') {
      const start = new Date(stored.start);
      const end = new Date(stored.end);
      if (!isNaN(start.getTime()) && !isNaN(end.getTime())) {
        return { preset: 'custom', start, end };
      }
      return this.fromPreset('1h');
    }

    if (stored.preset in PRESET_DURATIONS) {
      return this.fromPreset(stored.preset as TimePreset);
    }
    return this.fromPreset('1h');
  }

  private readStored(): StoredTimeRange | null {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      return JSON.parse(raw) as StoredTimeRange;
    } catch {
      return null;
    }
  }

  private persist(range: TimeRange): void {
    try {
      const stored: StoredTimeRange = {
        preset: range.preset,
        start: range.start.toISOString(),
        end: range.end.toISOString(),
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(stored));
    } catch {
      // ignore storage failures (e.g. private mode / quota)
    }
  }
}
