import { Component, HostListener, computed, inject, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatNativeDateModule } from '@angular/material/core';
import { FormsModule } from '@angular/forms';
import { TimePreset, TimeRangeService } from '../../../core/services/time-range.service';

@Component({
  selector: 'app-time-range-picker',
  standalone: true,
  imports: [
    MatIconModule, MatButtonModule, MatDatepickerModule,
    MatFormFieldModule, MatInputModule, MatNativeDateModule, FormsModule,
  ],
  template: `
    <div class="picker-wrap">
      <button mat-stroked-button class="trigger" (click)="togglePanel($event)">
        <mat-icon class="trigger-icon">schedule</mat-icon>
        <span class="trigger-label">{{ selectionLabel() }}</span>
        <mat-icon class="trigger-caret">{{ showPanel() ? 'arrow_drop_up' : 'arrow_drop_down' }}</mat-icon>
      </button>

      @if (showPanel()) {
        <div class="panel" (click)="$event.stopPropagation()">
          <div class="preset-list">
            @for (p of presets; track p.value) {
              <button mat-button class="preset-item"
                      [class.active]="timeRange.range().preset === p.value"
                      (click)="onPreset(p.value)">
                {{ p.label }}
              </button>
            }
          </div>

          <div class="custom-divider">Custom range</div>

          <div class="custom-row">
            <span class="custom-label">From</span>
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <input matInput [matDatepicker]="startPicker" [(ngModel)]="customStart" placeholder="Date">
              <mat-datepicker-toggle matIconSuffix [for]="startPicker" />
              <mat-datepicker #startPicker />
            </mat-form-field>
            <input class="time-input" type="time" [value]="toTimeString(customStart)"
                   (change)="onTimeChange('start', $event)" />
          </div>
          <div class="custom-row">
            <span class="custom-label">To</span>
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <input matInput [matDatepicker]="endPicker" [(ngModel)]="customEnd" placeholder="Date">
              <mat-datepicker-toggle matIconSuffix [for]="endPicker" />
              <mat-datepicker #endPicker />
            </mat-form-field>
            <input class="time-input" type="time" [value]="toTimeString(customEnd)"
                   (change)="onTimeChange('end', $event)" />
          </div>
          <div class="custom-actions">
            <button mat-button (click)="closePanel()">Cancel</button>
            <button mat-flat-button (click)="applyCustom()" [disabled]="!customStart || !customEnd">Apply</button>
          </div>
        </div>
      }
    </div>
  `,
  styles: [`
    .picker-wrap { position: relative; }

    .trigger {
      height: 36px;
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
    .trigger-icon { font-size: 18px; width: 18px; height: 18px; }
    .trigger-label { font-size: 13px; }
    .trigger-caret { font-size: 20px; width: 20px; height: 20px; margin-left: -2px; }

    .panel {
      position: absolute;
      top: calc(100% + 4px);
      right: 0;
      z-index: 11;
      background: var(--mat-sys-surface-container-high);
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 8px;
      padding: 8px;
      box-shadow: 0 4px 12px rgba(0,0,0,.15);
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-width: 340px;
    }

    .preset-list {
      display: flex;
      flex-direction: column;
    }
    .preset-item {
      justify-content: flex-start;
      text-align: left;
      font-size: 13px;
    }
    .preset-item.active {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }

    .custom-divider {
      font-size: 12px;
      font-weight: 500;
      color: var(--mat-sys-on-surface-variant);
      padding: 4px 8px 0;
      border-top: 1px solid var(--mat-sys-outline-variant);
      margin-top: 4px;
      padding-top: 8px;
    }

    .custom-row {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 0 4px;
    }

    .custom-label {
      width: 28px;
      font-size: 12px;
      color: var(--mat-sys-on-surface-variant);
    }

    .time-input {
      width: 80px;
      height: 36px;
      border: 1px solid var(--mat-sys-outline);
      border-radius: 4px;
      padding: 0 6px;
      font-size: 13px;
      background: transparent;
      color: var(--mat-sys-on-surface);
    }

    .custom-actions {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
      margin-top: 4px;
    }
  `],
})
export class TimeRangePickerComponent {
  protected readonly timeRange = inject(TimeRangeService);

  protected readonly presets: { value: TimePreset; label: string }[] = [
    { value: '1h', label: 'Last 1 Hour' },
    { value: '6h', label: 'Last 6 Hours' },
    { value: '24h', label: 'Last 24 Hours' },
    { value: '7d', label: 'Last 7 Days' },
    { value: '30d', label: 'Last 30 Days' },
  ];

  protected readonly showPanel = signal(false);

  protected readonly selectionLabel = computed(() => {
    const range = this.timeRange.range();
    if (range.preset === 'custom') {
      return `${this.formatRangeBound(range.start)} – ${this.formatRangeBound(range.end)}`;
    }
    return this.timeRange.presetLabel(range.preset);
  });

  protected customStart: Date | null = null;
  protected customEnd: Date | null = null;

  protected togglePanel(event: Event): void {
    event.stopPropagation();
    if (!this.showPanel()) {
      const { start, end } = this.timeRange.range();
      this.customStart = new Date(start);
      this.customEnd = new Date(end);
    }
    this.showPanel.update(v => !v);
  }

  protected closePanel(): void {
    this.showPanel.set(false);
  }

  @HostListener('document:click')
  protected onDocumentClick(): void {
    if (this.showPanel()) this.showPanel.set(false);
  }

  protected onPreset(value: TimePreset): void {
    this.timeRange.setPreset(value);
    this.showPanel.set(false);
  }

  protected toTimeString(d: Date | null): string {
    if (!d) return '00:00';
    return d.toTimeString().slice(0, 5);
  }

  protected onTimeChange(which: 'start' | 'end', event: Event): void {
    const [h, m] = (event.target as HTMLInputElement).value.split(':').map(Number);
    const base = which === 'start' ? this.customStart : this.customEnd;
    if (!base) return;
    const d = new Date(base);
    d.setHours(h, m, 0, 0);
    if (which === 'start') this.customStart = d;
    else this.customEnd = d;
  }

  protected applyCustom(): void {
    if (!this.customStart || !this.customEnd) return;
    this.showPanel.set(false);
    this.timeRange.setCustom(this.customStart, this.customEnd);
  }

  private formatRangeBound(d: Date): string {
    const date = d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    const time = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', hour12: false });
    return `${date} ${time}`;
  }
}
