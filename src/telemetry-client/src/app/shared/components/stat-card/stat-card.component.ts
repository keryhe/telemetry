import { Component, Input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { NgClass } from '@angular/common';

@Component({
  selector: 'app-stat-card',
  standalone: true,
  imports: [MatCardModule, MatIconModule, NgClass],
  template: `
    <mat-card class="stat-card">
      <mat-card-content>
        <div class="stat-row">
          <div class="stat-text">
            <div class="label">{{ label }}</div>
            <div class="value" [ngClass]="color">{{ displayValue }}</div>
            @if (subtitle) {
              <div class="subtitle">{{ subtitle }}</div>
            }
          </div>
          @if (icon) {
            <mat-icon class="stat-icon" [ngClass]="color">{{ icon }}</mat-icon>
          }
        </div>
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    .stat-card { height: 100%; }
    mat-card-content { padding: 16px !important; }
    .stat-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
    .stat-text { min-width: 0; }
    .label { font-size: 12px; color: var(--mat-sys-on-surface-variant); text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 4px; }
    .value { font-size: 28px; font-weight: 500; line-height: 1.2; }
    .subtitle { font-size: 12px; color: var(--mat-sys-on-surface-variant); margin-top: 4px; }
    .stat-icon { font-size: 36px; width: 36px; height: 36px; opacity: 0.7; color: var(--mat-sys-primary); flex-shrink: 0; }
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
  @Input() icon = '';
  @Input() color: 'default' | 'error' | 'warn' | 'success' = 'default';
}
