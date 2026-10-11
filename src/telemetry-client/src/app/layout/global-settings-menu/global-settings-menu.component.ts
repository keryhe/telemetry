import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ThemeMode, ThemeService } from '../../core/services/theme.service';

/**
 * The header's gear: a menu of global settings. They are per browser (localStorage, like the theme was), not
 * per user or tenant, so there is no API behind them. Each setting is one labelled row in the template: add a
 * future global setting as one more `.setting-row`. Sign out is not here; it lives in the user menu.
 */
@Component({
  selector: 'app-global-settings-menu',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, MatFormFieldModule, MatSelectModule],
  template: `
    <button mat-icon-button type="button" [matMenuTriggerFor]="menu" matTooltip="Settings" aria-label="Settings">
      <mat-icon>settings</mat-icon>
    </button>

    <mat-menu #menu="matMenu" class="global-settings-panel" xPosition="before">
      <!-- A click or key in a setting must not close the menu; Escape still does. -->
      <div class="setting-row" role="group" aria-label="Theme"
           (click)="$event.stopPropagation()" (keydown)="onKeydown($event)">
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Theme</mat-label>
          <mat-select [value]="themeService.mode()" (selectionChange)="themeService.setMode($event.value)">
            @for (mode of themeModes; track mode.value) {
              <mat-option [value]="mode.value">
                <mat-icon>{{ mode.icon }}</mat-icon>{{ mode.label }}
              </mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>
    </mat-menu>
  `,
  styles: [`
    .setting-row {
      padding: 8px 16px;
      min-width: 220px;
    }

    .setting-row mat-form-field {
      width: 100%;
    }

    mat-option mat-icon {
      margin-right: 12px;
    }
  `],
})
export class GlobalSettingsMenuComponent {
  protected readonly themeService = inject(ThemeService);

  protected readonly themeModes: { value: ThemeMode; icon: string; label: string }[] = [
    { value: 'light', icon: 'light_mode', label: 'Light' },
    { value: 'dark', icon: 'dark_mode', label: 'Dark' },
    { value: 'system', icon: 'contrast', label: 'System' },
  ];

  protected onKeydown(event: KeyboardEvent): void {
    // The menu's own key handling (arrow focus, typeahead) would steal keys meant for the select.
    if (event.key !== 'Escape') event.stopPropagation();
  }
}
