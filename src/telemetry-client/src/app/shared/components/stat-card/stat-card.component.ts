import { Component, Input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { NgClass } from '@angular/common';

@Component({
  selector: 'app-stat-card',
  standalone: true,
  imports: [MatCardModule, NgClass],
  template: `
    <mat-card class="stat-card">
      <mat-card-content>
        <div class="label">{{ label }}</div>
        <div class="value" [ngClass]="color">{{ displayValue }}</div>
        @if (subtitle) {
          <div class="subtitle">{{ subtitle }}</div>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    .stat-card { height: 100%; }
    mat-card-content { padding: 16px !important; }
    .label { font-size: 12px; color: var(--mat-sys-on-surface-variant); text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 4px; }
    .value { font-size: 28px; font-weight: 500; line-height: 1.2; }
    .subtitle { font-size: 12px; color: var(--mat-sys-on-surface-variant); margin-top: 4px; }
    .error { color: var(--mat-sys-error); }
    .warn { color: #ff9800; }
    .success { color: #4caf50; }
  `],
})
export class StatCardComponent {
  @Input() label = '';
  @Input() value: string | number | null = '—';
  get displayValue(): string | number { return this.value ?? '—'; }
  @Input() subtitle = '';
  @Input() color: 'default' | 'error' | 'warn' | 'success' = 'default';
}
