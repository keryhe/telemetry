import { Injectable, LOCALE_ID, inject } from '@angular/core';
import { formatNumber } from '@angular/common';
import { MatPaginatorIntl } from '@angular/material/paginator';

/**
 * Paginator range label with locale grouping ("1,501 – 2,000 of 51,698"), matching the stat
 * cards. Material's default label prints raw numbers. Provided app-wide in app.config.ts.
 */
@Injectable()
export class GroupedPaginatorIntl extends MatPaginatorIntl {
  private readonly locale = inject(LOCALE_ID);

  /**
   * Replaces the "of N" total when the page knows its total only as a lower bound (a summary timeout): the
   * paginator's length is then stretched past the current page and is not a count worth printing. A page that sets
   * this provides its own instance (component `providers`) and calls `changes.next()` after setting it.
   */
  totalOverride: string | null = null;

  override getRangeLabel = (page: number, pageSize: number, length: number): string => {
    const fmt = (n: number) => formatNumber(n, this.locale, '1.0-0');
    const total = this.totalOverride ?? fmt(length);
    if (length === 0 || pageSize === 0) return `0 of ${total}`;
    const start = page * pageSize;
    // Material's own clamp: a page past the end still shows a sensible range.
    const end = start < length ? Math.min(start + pageSize, length) : start + pageSize;
    return `${fmt(start + 1)} – ${fmt(end)} of ${total}`;
  };
}

/** The "of ..." text for a lower-bound total: "10,000+", or "many" when even the capped count timed out. */
export function lowerBoundTotalLabel(total: number, locale: string): string {
  return total > 0 ? `${formatNumber(total, locale, '1.0-0')}+` : 'many';
}
