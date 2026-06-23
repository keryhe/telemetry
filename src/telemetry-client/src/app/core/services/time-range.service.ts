import { Injectable, signal } from '@angular/core';

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

@Injectable({ providedIn: 'root' })
export class TimeRangeService {
  readonly range = signal<TimeRange>(this.loadInitial());

  setPreset(preset: TimePreset): void {
    const range = this.fromPreset(preset);
    this.range.set(range);
    this.persist(range);
  }

  setCustom(start: Date, end: Date): void {
    const range: TimeRange = { preset: 'custom', start, end };
    this.range.set(range);
    this.persist(range);
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
