import { Component, Input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { NgClass } from '@angular/common';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';

@Component({
  selector: 'app-stat-card',
  standalone: true,
  imports: [MatCardModule, MatIconModule, NgClass, NgApexchartsModule],
  template: `
    <mat-card class="stat-card" [class.clickable]="clickable">
      <mat-card-content>
        <div class="label">{{ label }}</div>
        <div class="stat-row">
          <div class="stat-text">
            <div class="value" [ngClass]="color">{{ displayValue }}</div>
            @if (subtitle) {
              <div class="subtitle">{{ subtitle }}</div>
            }
          </div>
          @if (sparklineOptions) {
            <apx-chart
              class="sparkline"
              [series]="sparklineOptions.series!"
              [chart]="sparklineOptions.chart!"
              [colors]="sparklineOptions.colors!"
              [stroke]="sparklineOptions.stroke!"
              [markers]="sparklineOptions.markers!"
              [tooltip]="sparklineOptions.tooltip!" />
          } @else if (icon) {
            <mat-icon class="stat-icon" [ngClass]="color">{{ icon }}</mat-icon>
          }
        </div>
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    /* Containment context so the sparkline rule below reacts to THIS card's own width rather
       than the viewport — cards in the same grid can be different widths. */
    .stat-card { height: 100%; container-type: inline-size; }
    .stat-card.clickable { cursor: pointer; }
    .stat-card.clickable:hover { background: var(--mat-sys-surface-container-high); }
    mat-card-content { padding: 16px !important; }
    .label { font-size: 12px; color: var(--mat-sys-on-surface-variant); text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 8px; white-space: nowrap; }
    .stat-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
    /* The number never gives up space; the sparkline beside it absorbs all the shrinking. With
       both shrinkable (the previous behaviour) flexbox squeezed them proportionally and the
       value's text — which has no overflow rule — spilled out over the chart. */
    .stat-text { flex-shrink: 0; }
    .value { font-size: 28px; font-weight: 500; line-height: 1.2; }
    .subtitle { font-size: 12px; color: var(--mat-sys-on-surface-variant); margin-top: 4px; }
    .stat-icon { font-size: 36px; width: 36px; height: 36px; opacity: 0.7; color: var(--mat-sys-primary); flex-shrink: 0; }
    /* A 100px flex-basis (rather than a fixed width) keeps the definite width ApexCharts needs
       to resolve its percentage width, while still allowing the item to shrink. */
    .sparkline { flex: 0 1 100px; min-width: 0; height: 40px; overflow: hidden; }
    /* Below this the sparkline would be too narrow to read anything from, so drop it rather than
       leave an unreadable sliver crowding the number. Container query (not a media query): it
       reacts to this card's own width. Tuned by measurement: a card is 32px of padding plus the
       value and a 12px gap, so on the widest-value card (146.6ms, approx 110px) a 200px card
       leaves the chart about 46px — around the floor of being readable. Cards bottom out at the
       180px grid minimum, where the chart would be about 30px. */
    @container (max-width: 200px) {
      .sparkline { display: none; }
    }
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
  /**
   * Optional trend line, built via `buildSparklineOptions`. Takes priority over `icon`.
   * Sizing is handled entirely in CSS: the chart is configured with a percentage width and
   * ApexCharts' own parent-resize observer redraws it when `.sparkline` shrinks.
   */
  @Input() sparklineOptions: ApexOptions | null = null;
  /** Purely visual: cursor + hover state for a card wrapped in a `routerLink`/click handler. */
  @Input() clickable = false;
}
