import { Injectable, signal } from '@angular/core';

export type TimePreset = '1h' | '4h' | '24h' | '7d' | '30d';

export interface TimeRange {
  preset: TimePreset | 'custom';
  start: Date;
  end: Date;
}

const PRESET_DURATIONS: Record<TimePreset, number> = {
  '1h':  60 * 60 * 1000,
  '4h':  4 * 60 * 60 * 1000,
  '24h': 24 * 60 * 60 * 1000,
  '7d':  7 * 24 * 60 * 60 * 1000,
  '30d': 30 * 24 * 60 * 60 * 1000,
};

@Injectable({ providedIn: 'root' })
export class TimeRangeService {
  readonly range = signal<TimeRange>(this.fromPreset('1h'));

  setPreset(preset: TimePreset): void {
    this.range.set(this.fromPreset(preset));
  }

  setCustom(start: Date, end: Date): void {
    this.range.set({ preset: 'custom', start, end });
  }

  private fromPreset(preset: TimePreset): TimeRange {
    const end = new Date();
    const start = new Date(end.getTime() - PRESET_DURATIONS[preset]);
    return { preset, start, end };
  }
}
