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

  override getRangeLabel = (page: number, pageSize: number, length: number): string => {
    const fmt = (n: number) => formatNumber(n, this.locale, '1.0-0');
    if (length === 0 || pageSize === 0) return `0 of ${fmt(length)}`;
    const start = page * pageSize;
    // Material's own clamp: a page past the end still shows a sensible range.
    const end = start < length ? Math.min(start + pageSize, length) : start + pageSize;
    return `${fmt(start + 1)} – ${fmt(end)} of ${fmt(length)}`;
  };
}
