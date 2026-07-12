import { Component } from '@angular/core';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'app-metric-search-help-dialog',
  standalone: true,
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>
      <mat-icon class="title-icon">help</mat-icon> Metric Search Guide
    </h2>
    <mat-dialog-content>
      <p class="lead">
        The search box filters the table by <strong>metric name</strong> — a case-insensitive
        match on any part of the name. Combine it with the <strong>Type</strong> and
        <strong>Service</strong> dropdowns to narrow the list further.
      </p>

      <h3 class="section">How Search Works</h3>
      <table class="help-table">
        <thead>
          <tr><th>You type</th><th>Matches</th></tr>
        </thead>
        <tbody>
          <tr>
            <td><code>http</code></td>
            <td>Every metric whose name contains "http" (e.g. <code>http.server.duration</code>)</td>
          </tr>
          <tr>
            <td><code>request.count</code></td>
            <td>Metrics whose name contains the substring "request.count"</td>
          </tr>
          <tr>
            <td><code>cpu</code></td>
            <td><code>system.cpu.utilization</code>, <code>process.cpu.time</code>, …</td>
          </tr>
        </tbody>
      </table>

      <h3 class="section">Combine With Filters</h3>
      <ul class="examples">
        <li><strong>Type</strong> — restrict to Gauges, Counters, Histograms, etc.</li>
        <li><strong>Service</strong> — show only metrics reported by one service</li>
        <li><strong>Search + filters</strong> — e.g. type <code>duration</code>, pick type <em>Histogram</em>, pick service <em>api-gateway</em></li>
      </ul>

      <div class="note info">
        <strong>Tip:</strong> Search matches the metric name only — not units, descriptions, or
        attribute values. Leave the box empty to see every metric in the current time range.
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button color="primary" mat-dialog-close>Got it!</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .title-icon { vertical-align: middle; margin-right: 4px; }
    .lead { font-size: 13px; margin: 0 0 8px; color: var(--mat-sys-on-surface-variant); }
    .section { font-size: 14px; font-weight: 600; margin: 16px 0 8px; }
    .help-table { width: 100%; border-collapse: collapse; font-size: 13px; }
    .help-table th, .help-table td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--mat-sys-outline-variant); vertical-align: top; }
    code { font-family: monospace; background: var(--mat-sys-surface-container-high); padding: 1px 5px; border-radius: 4px; font-size: 12px; }
    .examples { font-size: 13px; padding-left: 20px; margin: 0; }
    .examples li { margin-bottom: 6px; }
    .note { margin-top: 12px; padding: 10px 12px; border-radius: 6px; font-size: 13px; }
    .note.info { background: var(--mat-sys-secondary-container); color: var(--mat-sys-on-secondary-container); }
  `],
})
export class MetricSearchHelpDialogComponent {}
