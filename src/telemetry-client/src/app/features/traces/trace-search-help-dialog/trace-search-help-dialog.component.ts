import { Component } from '@angular/core';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'app-trace-search-help-dialog',
  standalone: true,
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>
      <mat-icon class="title-icon">help</mat-icon> Search Syntax Guide
    </h2>
    <mat-dialog-content>
      <h3 class="section">Search Types</h3>
      <table class="help-table">
        <thead>
          <tr><th>Type</th><th>Example</th><th>Description</th></tr>
        </thead>
        <tbody>
          <tr>
            <td><strong>Trace ID</strong></td>
            <td><code>abc123def456...</code></td>
            <td>Match a single trace by ID (32 hex chars)</td>
          </tr>
          <tr>
            <td><strong>Free Text</strong></td>
            <td><code>"database query"</code></td>
            <td>Searches the operation name and service name</td>
          </tr>
          <tr>
            <td><strong>Exact Match</strong></td>
            <td><code>http.status_code=500</code></td>
            <td>Some span in the trace has exactly this attribute value</td>
          </tr>
          <tr>
            <td><strong>Contains</strong></td>
            <td><code>url:'/api/users'</code></td>
            <td>Some span in the trace has an attribute value containing this text</td>
          </tr>
          <tr>
            <td><strong>Exclude</strong></td>
            <td><code>-http.method:GET</code></td>
            <td>Drop traces where <em>any</em> span matches</td>
          </tr>
          <tr>
            <td><strong>Multiple (AND)</strong></td>
            <td><code>"checkout" AND http.method=POST</code></td>
            <td>All conditions must match</td>
          </tr>
        </tbody>
      </table>

      <h3 class="section">Examples</h3>
      <ul class="examples">
        <li><code>a1b2c3d4e5f6...</code> — Jump to a specific trace ID</li>
        <li><code>checkout</code> — Find traces whose operation or service contains "checkout"</li>
        <li><code>http.status_code=500</code> — Traces containing a span with status code exactly 500</li>
        <li><code>http.method:POST</code> — Traces containing a span whose method contains "POST"</li>
        <li><code>-http.method:GET</code> — Traces with no GET span at all</li>
        <li><code>"place order" AND service.name=api-gateway</code> — Combine conditions</li>
      </ul>

      <div class="note info">
        <strong>Tip:</strong> Attribute searches look at every span in the trace — its own
        attributes and its resource attributes — not just the root span. A trace matches when
        <em>some</em> span satisfies the condition, so the matching span may be several levels
        deep. Attribute keys are case-sensitive; values are not.
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
export class TraceSearchHelpDialogComponent {}
