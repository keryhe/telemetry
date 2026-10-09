import { Component, input } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';

/**
 * The line above a capped list (logs, traces, metric catalog, exemplars) that has more matching rows than the server returns.
 * The caller shows it only when the response says `truncated`; `limit` is the server's configured cap, from capabilities.
 */
@Component({
  selector: 'app-list-cap-notice',
  standalone: true,
  imports: [DecimalPipe, MatIconModule],
  template: `
    <div class="cap-notice">
      <mat-icon>info</mat-icon>
      <span>
        Showing the {{ order() }} {{ limit() | number }} matching {{ noun() }}. Narrow the time range or add filters to see others.
      </span>
    </div>
  `,
  styles: `
    .cap-notice {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 12px;
      margin-bottom: 8px;
      font-size: 13px;
      color: var(--mat-sys-on-surface-variant);
      background: var(--mat-sys-surface-container-high);
      border-radius: 6px;
    }
    mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
      flex-shrink: 0;
      color: var(--mat-sys-primary);
    }
  `,
})
export class ListCapNoticeComponent {
  /** What the list holds, plural: "logs", "traces", "metrics", "exemplars". */
  readonly noun = input.required<string>();
  /** The server's cap for this list. */
  readonly limit = input.required<number>();
  readonly order = input<'newest' | 'oldest'>('newest');
}
