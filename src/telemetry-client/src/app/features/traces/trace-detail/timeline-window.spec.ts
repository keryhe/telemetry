import { rowTop, rowWindow, RowWindowInput } from './timeline-window';

const base: RowWindowInput = {
  count: 1000, rowHeight: 35, viewTop: 0, viewBottom: 700, selectedIndex: -1, detailHeight: 0, buffer: 5,
};

/** Rendered rows plus both spacers (plus the open detail, when it is rendered) must add up to the whole list. */
function totalHeight(i: RowWindowInput, w: { first: number; last: number; topPad: number; bottomPad: number }): number {
  const renderedRows = (w.last - w.first + 1) * i.rowHeight;
  const detailRendered = i.selectedIndex >= w.first && i.selectedIndex <= w.last ? i.detailHeight : 0;
  return w.topPad + renderedRows + detailRendered + w.bottomPad;
}

describe('rowWindow', () => {
  it('starts at the top with only a bottom spacer', () => {
    const w = rowWindow(base);
    expect(w.first).toBe(0);
    expect(w.topPad).toBe(0);
    expect(w.last).toBe(20 + 5); // the row at 700px, plus the buffer
    expect(totalHeight(base, w)).toBe(1000 * 35);
  });

  it('renders the rows around the viewport in the middle of the list', () => {
    const i = { ...base, viewTop: 17_500, viewBottom: 18_200 };
    const w = rowWindow(i);
    expect(w.first).toBe(500 - 5);
    expect(w.last).toBe(520 + 5);
    expect(w.topPad).toBe(w.first * 35);
    expect(totalHeight(i, w)).toBe(1000 * 35);
  });

  it('clamps at the end of the list', () => {
    const i = { ...base, viewTop: 34_300, viewBottom: 35_000 };
    const w = rowWindow(i);
    expect(w.last).toBe(999);
    expect(w.bottomPad).toBe(0);
    expect(totalHeight(i, w)).toBe(1000 * 35);
  });

  it('clamps a viewport scrolled past either end', () => {
    expect(rowWindow({ ...base, viewTop: -500, viewBottom: 200 }).first).toBe(0);
    const past = rowWindow({ ...base, viewTop: 900_000, viewBottom: 901_000 });
    expect(past.last).toBe(999);
    expect(past.first).toBeLessThanOrEqual(999);
  });

  it('keeps the sum constant with a detail block open above the viewport', () => {
    const i = { ...base, viewTop: 20_000, viewBottom: 20_700, selectedIndex: 100, detailHeight: 600 };
    const w = rowWindow(i);
    // The 600px detail pushes the rows after it down, so the same y lands on an earlier row.
    expect(w.first).toBe(Math.floor((20_000 - 600) / 35) - 5);
    expect(totalHeight(i, w)).toBe(1000 * 35 + 600);
  });

  it('renders the selected row and its detail when the viewport is inside the detail', () => {
    const i = { ...base, viewTop: 100 * 35 + 35 + 100, viewBottom: 100 * 35 + 35 + 400, selectedIndex: 100, detailHeight: 600 };
    const w = rowWindow(i);
    expect(w.first).toBeLessThanOrEqual(100);
    expect(w.last).toBeGreaterThanOrEqual(100);
    expect(totalHeight(i, w)).toBe(1000 * 35 + 600);
  });

  it('keeps the sum constant with the detail open below the viewport (not rendered)', () => {
    const i = { ...base, viewTop: 0, viewBottom: 700, selectedIndex: 800, detailHeight: 600 };
    const w = rowWindow(i);
    expect(w.last).toBeLessThan(800);
    expect(totalHeight(i, w)).toBe(1000 * 35 + 600);
  });

  it('handles an empty list and a list shorter than the window', () => {
    expect(rowWindow({ ...base, count: 0 })).toEqual({ first: 0, last: -1, topPad: 0, bottomPad: 0 });
    const small = rowWindow({ ...base, count: 3 });
    expect(small.first).toBe(0);
    expect(small.last).toBe(2);
    expect(small.bottomPad).toBe(0);
  });

  it('places rows after the selected row below its detail', () => {
    expect(rowTop(5, 35, 10, 600)).toBe(175);
    expect(rowTop(10, 35, 10, 600)).toBe(350);
    expect(rowTop(11, 35, 10, 600)).toBe(11 * 35 + 600);
    expect(rowTop(11, 35, -1, 600)).toBe(11 * 35);
  });
});
