import { Component } from '@angular/core';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'app-span-search-help-dialog',
  standalone: true,
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>
      <mat-icon class="title-icon">help</mat-icon> Find Spans Guide
    </h2>
    <mat-dialog-content>
      <h3 class="section">Search Types</h3>
      <table class="help-table">
        <thead>
          <tr><th>Type</th><th>Example</th><th>Description</th></tr>
        </thead>
        <tbody>
          <tr>
            <td><strong>Free Text</strong></td>
            <td><code>database query</code></td>
            <td>Matches the span name or service name</td>
          </tr>
          <tr>
            <td><strong>Name</strong></td>
            <td><code>name:GET</code></td>
            <td>Span operation name contains text (also <code>op</code>, <code>operation</code>)</td>
          </tr>
          <tr>
            <td><strong>Service</strong></td>
            <td><code>service:api</code></td>
            <td>Service name contains text (also <code>service.name</code>)</td>
          </tr>
          <tr>
            <td><strong>Attribute (contains)</strong></td>
            <td><code>http.status_code:500</code></td>
            <td>Span or resource attribute value contains text</td>
          </tr>
          <tr>
            <td><strong>Attribute (exact)</strong></td>
            <td><code>http.method=POST</code></td>
            <td>Attribute value matches exactly</td>
          </tr>
          <tr>
            <td><strong>Min Duration</strong></td>
            <td><code>min-duration:100ms</code></td>
            <td>Spans at or above a duration threshold (also <code>duration</code>)</td>
          </tr>
          <tr>
            <td><strong>Span / Trace ID</strong></td>
            <td><code>a1b2c3d4...</code></td>
            <td>Match spans whose span or trace ID contains the hex</td>
          </tr>
          <tr>
            <td><strong>Multiple (AND)</strong></td>
            <td><code>service:api min-duration:50ms</code></td>
            <td>All conditions must match</td>
          </tr>
        </tbody>
      </table>

      <h3 class="section">Examples</h3>
      <ul class="examples">
        <li><code>checkout</code> — Spans whose name or service contains "checkout"</li>
        <li><code>service:api http.status_code:500</code> — Failing spans in the api service</li>
        <li><code>min-duration:1s</code> — Spans lasting at least one second</li>
        <li><code>http.method=GET</code> — Spans with the method attribute exactly "GET"</li>
      </ul>

      <div class="note info">
        <strong>Tip:</strong> Durations accept a bare number or <code>ms</code> (e.g. <code>500</code>, <code>500ms</code>),
        and seconds when suffixed with <code>s</code> (e.g. <code>1.5s</code>). Press <strong>Enter</strong> to jump to the next match.
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button color="primary" mat-dialog-close>Got it!</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .title-icon { vertical-align: middle; margin-right: 4px; }
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
export class SpanSearchHelpDialogComponent {}
