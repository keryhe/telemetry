import { Component, DestroyRef, OnInit, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';

import { SettingsApiService } from '../../core/services/api/settings-api.service';
import { RetentionSettings } from '../../core/models/settings.models';
import { TenantService } from '../../core/services/tenant.service';

type RetentionFormValue = Pick<RetentionSettings, 'traceRetentionDays' | 'logRetentionDays' | 'metricRetentionDays'>;

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [
    ReactiveFormsModule, DatePipe,
    MatCardModule, MatButtonModule, MatIconModule, MatFormFieldModule,
    MatInputModule, MatProgressBarModule, MatTooltipModule,
  ],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
})
export class SettingsComponent implements OnInit {
  protected readonly tenantService = inject(TenantService);
  private readonly api = inject(SettingsApiService);
  private readonly fb = inject(FormBuilder);
  private readonly snack = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  protected loading = signal(true);
  protected saving = signal(false);

  // Upper bound mirrors TenantSettingsController.MaxRetentionDays — a client-side check that fails
  // fast on an obvious typo, not the source of truth for what the server accepts.
  private readonly maxRetentionDays = 3650;

  protected readonly retentionForm = this.fb.nonNullable.group({
    traceRetentionDays: [30, [Validators.required, Validators.min(1), Validators.max(this.maxRetentionDays)]],
    logRetentionDays: [30, [Validators.required, Validators.min(1), Validators.max(this.maxRetentionDays)]],
    metricRetentionDays: [30, [Validators.required, Validators.min(1), Validators.max(this.maxRetentionDays)]],
  });

  private lastSaved: RetentionFormValue = this.retentionForm.getRawValue();

  protected readonly dirtyFields = signal<Set<keyof RetentionFormValue>>(new Set());
  protected readonly retentionDirty = signal(false);

  protected readonly updatedAt = signal<string | null>(null);

  constructor() {
    // The page follows the route's tenant: a tenant switch loads that tenant's windows.
    let first = true;
    effect(() => {
      this.tenantService.routeTenantId();
      if (first) { first = false; return; }
      untracked(() => this.load());
    });
  }

  ngOnInit(): void {
    this.load();

    this.retentionForm.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((value) => {
      const dirty = new Set<keyof RetentionFormValue>();
      for (const key of Object.keys(this.lastSaved) as (keyof RetentionFormValue)[]) {
        if (value[key] !== this.lastSaved[key]) {
          dirty.add(key);
        }
      }
      this.dirtyFields.set(dirty);
      this.retentionDirty.set(dirty.size > 0);
    });
  }

  private load(): void {
    this.loading.set(true);
    this.api.getRetentionSettings().subscribe({
      next: (settings) => {
        this.retentionForm.patchValue(settings, { emitEvent: false });
        this.lastSaved = this.retentionForm.getRawValue();
        this.updatedAt.set(settings.updatedAt ?? null);
        this.dirtyFields.set(new Set());
        this.retentionDirty.set(false);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected isFieldDirty(field: keyof RetentionFormValue): boolean {
    return this.dirtyFields().has(field);
  }

  protected cancelRetention(): void {
    this.retentionForm.reset(this.lastSaved);
  }

  protected saveRetention(): void {
    if (this.retentionForm.invalid) {
      this.retentionForm.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const value = this.retentionForm.getRawValue();
    this.api.updateRetentionSettings(value).subscribe({
      next: (settings) => {
        this.retentionForm.patchValue(settings);
        this.lastSaved = this.retentionForm.getRawValue();
        this.updatedAt.set(settings.updatedAt ?? null);
        this.dirtyFields.set(new Set());
        this.retentionDirty.set(false);
        this.saving.set(false);
        this.snack.open('Retention settings saved', undefined, { duration: 3000 });
      },
      error: () => {
        this.saving.set(false);
        this.snack.open('Failed to save retention settings', undefined, { duration: 3000 });
      },
    });
  }
}
