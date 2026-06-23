import { Component, Input } from '@angular/core';
import { getSeverityColor, getSeverityLabel } from '../../../core/models/log.models';

@Component({
  selector: 'app-severity-badge',
  standalone: true,
  template: `
    <span class="badge" [style.background-color]="color" [style.color]="'#fff'">
      {{ label }}
    </span>
  `,
  styles: [`
    .badge {
      display: inline-block;
      padding: 2px 8px;
      border-radius: 4px;
      font-size: 11px;
      font-weight: 500;
      letter-spacing: 0.3px;
      white-space: nowrap;
    }
  `],
})
export class SeverityBadgeComponent {
  @Input() set severity(num: number | null) {
    this.label = getSeverityLabel(num);
    this.color = getSeverityColor(num);
  }

  label = 'Unknown';
  color = '#9e9e9e';
}
