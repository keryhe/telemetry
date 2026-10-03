/**
 * Row windowing for the trace timeline (trace-list-detail-performance plan, Phase 6d): which rows of a long, equal-height list
 * to put in the DOM, and the spacer heights that stand in for the rest. Pure, so the arithmetic is tested on its own.
 *
 * One row (the selected span's) can be followed by an inline detail block of a different height. Rows after it sit
 * `detailHeight` lower; the detail itself is rendered with its row.
 */
export interface RowWindowInput {
  /** Number of rows in the list. */
  count: number;
  /** Height of one row, px. */
  rowHeight: number;
  /** The visible range, px from the top of the rows container. */
  viewTop: number;
  viewBottom: number;
  /** Index of the row with an open detail block, or -1. */
  selectedIndex: number;
  /** Height of that detail block, px. */
  detailHeight: number;
  /** Rows rendered beyond each edge of the visible range. */
  buffer: number;
}

export interface RowWindow {
  /** First and last row index to render (inclusive). With no rows, `last` is -1. */
  first: number;
  last: number;
  /** Spacer heights before the first and after the last rendered row, px. */
  topPad: number;
  bottomPad: number;
}

/** Top of row `index` within the container, px. */
export function rowTop(index: number, rowHeight: number, selectedIndex: number, detailHeight: number): number {
  return index * rowHeight + (selectedIndex >= 0 && index > selectedIndex ? detailHeight : 0);
}

/** The row at height `y` (the selected row when `y` is inside its detail block). */
function indexAt(y: number, count: number, rowHeight: number, selectedIndex: number, detailHeight: number): number {
  if (y <= 0) return 0;
  if (selectedIndex < 0 || y < (selectedIndex + 1) * rowHeight) return Math.min(count - 1, Math.floor(y / rowHeight));
  if (y < (selectedIndex + 1) * rowHeight + detailHeight) return selectedIndex;
  return Math.min(count - 1, Math.floor((y - detailHeight) / rowHeight));
}

export function rowWindow(i: RowWindowInput): RowWindow {
  if (i.count <= 0) return { first: 0, last: -1, topPad: 0, bottomPad: 0 };
  const at = (y: number) => indexAt(y, i.count, i.rowHeight, i.selectedIndex, i.detailHeight);
  const first = Math.max(0, at(i.viewTop) - i.buffer);
  const last = Math.min(i.count - 1, at(i.viewBottom) + i.buffer);
  const total = i.count * i.rowHeight + (i.selectedIndex >= 0 ? i.detailHeight : 0);
  const renderedBottom = rowTop(last, i.rowHeight, i.selectedIndex, i.detailHeight) + i.rowHeight
    + (i.selectedIndex === last ? i.detailHeight : 0);
  return {
    first,
    last,
    topPad: rowTop(first, i.rowHeight, i.selectedIndex, i.detailHeight),
    bottomPad: Math.max(0, total - renderedBottom),
  };
}
