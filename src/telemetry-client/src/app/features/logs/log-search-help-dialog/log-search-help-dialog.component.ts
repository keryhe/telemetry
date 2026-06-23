import { Component } from '@angular/core';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'app-log-search-help-dialog',
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
            <td>Search by trace ID (32 hex chars, date range not required)</td>
          </tr>
          <tr>
            <td><strong>Free Text</strong></td>
            <td><code>"database error"</code></td>
            <td>Searches in log message body</td>
          </tr>
          <tr>
            <td><strong>Exact Match</strong></td>
            <td><code>http.status_code=500</code></td>
            <td>Exact attribute value match</td>
          </tr>
          <tr>
            <td><strong>Contains</strong></td>
            <td><code>url:'/api/users'</code></td>
            <td>Attribute value contains text</td>
          </tr>
          <tr>
            <td><strong>Multiple (AND)</strong></td>
            <td><code>"error" AND status_code=500</code></td>
            <td>All conditions must match</td>
          </tr>
        </tbody>
      </table>

      <h3 class="section">Examples</h3>
      <ul class="examples">
        <li><code>a1b2c3d4e5f6...</code> — Find all logs for a specific trace ID</li>
        <li><code>database timeout</code> — Find logs with "database timeout" in message</li>
        <li><code>http.status_code=500</code> — Find logs with status code exactly 500</li>
        <li><code>thread.id:5</code> — Find logs where thread.id contains "5"</li>
        <li><code>"connection failed" AND service.name=api-gateway</code> — Combine conditions</li>
      </ul>

      <div class="note info">
        <strong>Tip:</strong> Attribute searches look in Log Attributes, Resource Attributes, and Scope Attributes.
      </div>
      <div class="note success">
        <strong>Trace ID Search:</strong> When searching by trace ID (exactly 32 hex characters),
        the date range is optional and will be ignored. This allows you to find logs across any time period.
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
    .note.success { background: var(--mat-sys-tertiary-container); color: var(--mat-sys-on-tertiary-container); }
  `],
})
export class LogSearchHelpDialogComponent {}
