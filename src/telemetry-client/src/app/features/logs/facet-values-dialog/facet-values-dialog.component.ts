import { Component, Signal, computed, inject, signal } from '@angular/core';
import { DecimalPipe, PercentPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';

import { FacetValue, FacetValueType } from '../facet.models';
import { buildAttributeTerm } from '../../../shared/utils/search-query.parser';

/** Data contract for the "show all values" dialog. The parent passes its live `searchText`
 *  signal and a bound toggle so include/exclude apply immediately to both surfaces. */
export interface FacetValuesDialogData {
  key: string;
  type: FacetValueType;
  values: FacetValue[];
  distinct: number;
  total: number;
  searchText: Signal<string>;
  toggle: (key: string, value: string, exclude: boolean) => void;
}

/** Split a search string into its ` AND `-separated terms (mirrors logs.component's helper). */
function splitTerms(query: string): string[] {
  const t = query.trim();
  return t ? t.split(' AND ').map((s) => s.trim()).filter(Boolean) : [];
}

const TYPE_ICON: Record<FacetValueType, string> = { string: 'abc', number: 'tag', boolean: 'toggle_on' };

@Component({
  selector: 'app-facet-values-dialog',
  standalone: true,
  imports: [
    DecimalPipe, PercentPipe, FormsModule, ScrollingModule,
    MatDialogModule, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule,
    MatTooltipModule,
  ],
  template: `
    <h2 mat-dialog-title>
      <mat-icon class="type-icon">{{ typeIcon }}</mat-icon>
      <span class="mono">{{ data.key }}</span>
      <span class="count-caption">{{ data.distinct | number }} values</span>
    </h2>

    <mat-dialog-content class="dialog-body">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="value-search">
        <mat-icon matPrefix>search</mat-icon>
        <input matInput [ngModel]="filter()" (ngModelChange)="filter.set($event)"
               placeholder="Filter values" />
      </mat-form-field>

      @if (rows().length === 0) {
        <div class="empty">No values match "{{ filter() }}"</div>
      } @else {
        <cdk-virtual-scroll-viewport itemSize="34" class="value-viewport">
          <div class="facet-value" *cdkVirtualFor="let v of rows()"
               [class.active]="v.active" [class.excluded]="v.excluded">
            <div class="bar" [class.excluded]="v.excluded" [style.width.%]="v.pct"></div>
            <button class="value-main" (click)="data.toggle(data.key, v.value, false)"
                    [matTooltip]="v.value">
              <span class="value-text">{{ v.value }}</span>
              <span class="value-count">{{ v.count | number }} · {{ v.share | percent:'1.0-1' }}</span>
            </button>
            <button class="act include" (click)="data.toggle(data.key, v.value, false)"
                    matTooltip="Include" aria-label="Include value">
              <mat-icon>add</mat-icon>
            </button>
            <button class="act exclude" (click)="data.toggle(data.key, v.value, true)"
                    matTooltip="Exclude" aria-label="Exclude value">
              <mat-icon>block</mat-icon>
            </button>
          </div>
        </cdk-virtual-scroll-viewport>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-flat-button color="primary" mat-dialog-close>Done</button>
    </mat-dialog-actions>
  `,
  styles: [`
    [mat-dialog-title] { display: flex; align-items: center; gap: 8px; }
    .type-icon { font-size: 20px; width: 20px; height: 20px; color: var(--mat-sys-on-surface-variant); }
    .mono { font-family: monospace; }
    .count-caption { margin-left: auto; font-size: 12px; font-weight: 400; color: var(--mat-sys-on-surface-variant); }

    .dialog-body { display: flex; flex-direction: column; }
    .value-search { width: 100%; }
    .value-search mat-icon[matPrefix] { margin-right: 6px; opacity: 0.6; }
    .empty { padding: 24px 8px; font-size: 13px; color: var(--mat-sys-on-surface-variant); text-align: center; }

    .value-viewport { height: 50vh; max-height: 420px; }

    .facet-value {
      position: relative;
      display: flex; align-items: center; height: 34px;
      border-radius: 4px;
      &:hover { background: var(--mat-sys-surface-container-high); }
      &.active .value-text { color: var(--mat-sys-primary); font-weight: 600; }
      &.excluded .value-text { color: var(--mat-sys-error); text-decoration: line-through; }
    }
    .bar {
      position: absolute; left: 0; top: 3px; bottom: 3px;
      background: color-mix(in srgb, var(--mat-sys-primary) 14%, transparent);
      border-radius: 3px; pointer-events: none;
      &.excluded { background: color-mix(in srgb, var(--mat-sys-error) 14%, transparent); }
    }
    .value-main {
      position: relative; z-index: 1;
      flex: 1; min-width: 0;
      display: flex; align-items: center; gap: 8px;
      border: none; background: none; cursor: pointer;
      padding: 0 8px; height: 100%;
      color: var(--mat-sys-on-surface); text-align: left;
      .value-text { flex: 1; min-width: 0; font-size: 13px; font-family: monospace; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .value-count { font-size: 11px; color: var(--mat-sys-on-surface-variant); font-variant-numeric: tabular-nums; white-space: nowrap; }
    }
    .act {
      position: relative; z-index: 1;
      border: none; background: none; cursor: pointer;
      display: flex; align-items: center; padding: 2px 4px;
      color: var(--mat-sys-on-surface-variant);
      mat-icon { font-size: 16px; width: 16px; height: 16px; }
    }
    .act.include:hover { color: var(--mat-sys-primary); }
    .act.exclude:hover { color: var(--mat-sys-error); }
  `],
})
export class FacetValuesDialogComponent {
  protected readonly data = inject<FacetValuesDialogData>(MAT_DIALOG_DATA);
  protected readonly filter = signal('');
  protected readonly typeIcon = TYPE_ICON[this.data.type] ?? 'abc';

  /** Values filtered by the local search, with active/excluded recomputed live from the parent's search box. */
  protected readonly rows = computed<FacetValue[]>(() => {
    const q = this.filter().trim().toLowerCase();
    const parts = new Set(splitTerms(this.data.searchText()));
    const base = q ? this.data.values.filter((v) => v.value.toLowerCase().includes(q)) : this.data.values;
    return base.map((v) => ({
      ...v,
      active: parts.has(buildAttributeTerm(this.data.key, v.value, false)),
      excluded: parts.has(buildAttributeTerm(this.data.key, v.value, true)),
    }));
  });
}
