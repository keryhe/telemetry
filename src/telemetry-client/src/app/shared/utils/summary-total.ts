import { formatNumber } from '@angular/common';

/**
 * A trace/log summary's total for display. After a server-side summary timeout the total is a capped count: exact
 * below the cap, the cap itself as a lower bound above it ("10,000+"), or unknown -- a lower bound of 0 -- when the
 * capped count ran out of time too ("—").
 */
export function formatSummaryTotal(total: number, isLowerBound: boolean, locale: string): string {
  if (isLowerBound && total === 0) return '—';
  const n = formatNumber(total, locale, '1.0-0');
  return isLowerBound ? `${n}+` : n;
}

/** Tooltip for a stat card whose value is unavailable because the summary timed out. */
export const SUMMARY_TIMEOUT_TOOLTIP = 'Unavailable: the summary for this time range timed out. Narrow the time range or filter by service.';
