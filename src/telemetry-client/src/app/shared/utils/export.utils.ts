/**
 * Client-side export helpers shared by the logs and trace pages: RFC-4180 CSV / JSON
 * serialization, a file-download trigger, and a clipboard permalink. Mirrors the ad-hoc
 * exportCsv() in metric-detail and downloadJson() in trace-detail so every export
 * affordance across the app behaves and names files consistently.
 */

type CsvValue = string | number | boolean | null | undefined;

/** Quote a CSV field only when it contains a comma, quote, or newline (RFC-4180 escaping). */
export function csvEscape(value: CsvValue): string {
  const s = value == null ? '' : String(value);
  return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

/** Build an RFC-4180 CSV string from a header row and pre-mapped data rows. */
export function buildCsv(headers: string[], rows: CsvValue[][]): string {
  const lines = [headers.map(csvEscape).join(',')];
  for (const row of rows) lines.push(row.map(csvEscape).join(','));
  return lines.join('\n');
}

/** Trigger a client-side download of arbitrary text as a named file. */
export function downloadText(filename: string, text: string, mime: string): void {
  const blob = new Blob([text], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

/** Serialize rows to CSV and download them. */
export function downloadCsv(filename: string, headers: string[], rows: CsvValue[][]): void {
  downloadText(filename, buildCsv(headers, rows), 'text/csv;charset=utf-8;');
}

/** Pretty-print a value to JSON and download it. */
export function downloadJson(filename: string, data: unknown): void {
  downloadText(filename, JSON.stringify(data, null, 2), 'application/json');
}

/** Compact UTC stamp (yyyyMMddTHHmmssZ-ish) for export filenames — matches metric-detail's scheme. */
export function fileStamp(): string {
  return new Date().toISOString().replace(/[:.]/g, '').slice(0, 15) + 'Z';
}

/** Copy text (default: the current URL) to the clipboard; returns the clipboard promise. */
export function copyPermalink(text: string = window.location.href): Promise<void> {
  return navigator.clipboard?.writeText(text) ?? Promise.reject(new Error('Clipboard unavailable'));
}
