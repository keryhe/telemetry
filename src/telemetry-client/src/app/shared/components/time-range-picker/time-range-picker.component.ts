import { Component, inject } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
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
    MatButtonToggleModule, MatIconModule, MatMenuModule,
    MatButtonModule, MatDatepickerModule, MatFormFieldModule,
    MatInputModule, MatNativeDateModule, FormsModule,
  ],
  template: `
    <mat-button-toggle-group [value]="timeRange.range().preset" (change)="onPreset($event.value)">
      @for (p of presets; track p.value) {
        <mat-button-toggle [value]="p.value">{{ p.label }}</mat-button-toggle>
      }
    </mat-button-toggle-group>
  `,
  styles: [`
    mat-button-toggle-group { height: 36px; }
    :host ::ng-deep .mat-button-toggle-label-content { line-height: 36px; padding: 0 10px; font-size: 13px; }
  `],
})
export class TimeRangePickerComponent {
  protected readonly timeRange = inject(TimeRangeService);

  protected readonly presets: { value: TimePreset; label: string }[] = [
    { value: '1h', label: '1H' },
    { value: '4h', label: '4H' },
    { value: '24h', label: '24H' },
    { value: '7d', label: '7D' },
    { value: '30d', label: '30D' },
  ];

  protected onPreset(preset: TimePreset): void {
    this.timeRange.setPreset(preset);
  }
}
