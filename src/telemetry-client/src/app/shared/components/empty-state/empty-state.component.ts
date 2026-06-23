import { Component, Input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'app-empty-state',
  standalone: true,
  imports: [MatIconModule],
  template: `
    <div class="empty-state">
      <mat-icon class="icon">{{ icon }}</mat-icon>
      <div class="message">{{ message }}</div>
      @if (hint) {
        <div class="hint">{{ hint }}</div>
      }
    </div>
  `,
  styles: [`
    .empty-state {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      padding: 48px 16px;
      color: var(--mat-sys-on-surface-variant);
    }
    .icon { font-size: 48px; width: 48px; height: 48px; margin-bottom: 16px; opacity: 0.4; }
    .message { font-size: 16px; font-weight: 500; }
    .hint { font-size: 14px; margin-top: 8px; opacity: 0.7; }
  `],
})
export class EmptyStateComponent {
  @Input() icon = 'inbox';
  @Input() message = 'No data';
  @Input() hint = '';
}
