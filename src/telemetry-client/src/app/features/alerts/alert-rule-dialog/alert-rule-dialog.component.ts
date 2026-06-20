import { Component, OnInit, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';

import { AlertRule, AlertRuleType, ALERT_RULE_TYPE_LABELS, parseCondition, MetricThresholdCondition, ThresholdCondition } from '../../../core/models/alert.models';

export interface AlertRuleDialogData {
  rule?: AlertRule;
}

@Component({
  selector: 'app-alert-rule-dialog',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatDialogModule, MatButtonModule, MatFormFieldModule,
    MatInputModule, MatSelectModule, MatSlideToggleModule, MatIconModule,
  ],
  template: `
    <h2 mat-dialog-title>{{ data.rule ? 'Edit' : 'Create' }} Alert Rule</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="dialog-form">
        <mat-form-field appearance="outline" class="full-width">
          <mat-label>Name</mat-label>
          <input matInput formControlName="name" placeholder="My Alert Rule" />
        </mat-form-field>

        <mat-form-field appearance="outline" class="full-width">
          <mat-label>Type</mat-label>
          <mat-select formControlName="type">
            @for (entry of ruleTypes; track entry.value) {
              <mat-option [value]="entry.value">{{ entry.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field appearance="outline" class="full-width">
          <mat-label>Service (optional)</mat-label>
          <input matInput formControlName="serviceName" placeholder="All services" />
        </mat-form-field>

        @if (isMetricThreshold()) {
          <mat-form-field appearance="outline" class="full-width">
            <mat-label>Metric Name</mat-label>
            <input matInput formControlName="metricName" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Operator</mat-label>
            <mat-select formControlName="operator">
              <mat-option value="gt">&gt; (greater than)</mat-option>
              <mat-option value="lt">&lt; (less than)</mat-option>
              <mat-option value="gte">&ge;</mat-option>
              <mat-option value="lte">&le;</mat-option>
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Threshold</mat-label>
            <input matInput type="number" formControlName="threshold" />
          </mat-form-field>
        }

        @if (!isMetricThreshold()) {
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>{{ thresholdLabel() }}</mat-label>
            <input matInput type="number" formControlName="threshold" />
          </mat-form-field>
        }

        <mat-form-field appearance="outline" class="half-width">
          <mat-label>Cooldown (minutes)</mat-label>
          <input matInput type="number" formControlName="cooldownMinutes" />
        </mat-form-field>

        <mat-form-field appearance="outline" class="full-width">
          <mat-label>Webhook URL</mat-label>
          <input matInput formControlName="webhookUrl" placeholder="https://…" />
        </mat-form-field>

        <mat-slide-toggle formControlName="enabled">Enabled</mat-slide-toggle>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button color="primary" [disabled]="form.invalid" (click)="save()">
        {{ data.rule ? 'Save' : 'Create' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .dialog-form { display: flex; flex-direction: column; gap: 4px; padding-top: 4px; min-width: 480px; }
    .full-width { width: 100%; }
    .half-width { width: calc(50% - 4px); }
  `],
})
export class AlertRuleDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  readonly dialogRef = inject(MatDialogRef<AlertRuleDialogComponent>);
  readonly data: AlertRuleDialogData = inject(MAT_DIALOG_DATA);

  protected form = this.fb.group({
    name: ['', Validators.required],
    type: [AlertRuleType.MetricThreshold, Validators.required],
    serviceName: [''],
    metricName: [''],
    operator: ['gt'],
    threshold: [0, [Validators.required, Validators.min(0)]],
    cooldownMinutes: [30, Validators.min(1)],
    webhookUrl: [''],
    enabled: [true],
  });

  protected readonly ruleTypes = Object.entries(ALERT_RULE_TYPE_LABELS).map(([v, label]) => ({
    value: Number(v), label,
  }));

  protected isMetricThreshold = () => this.form.value.type === AlertRuleType.MetricThreshold;

  protected thresholdLabel = () => {
    switch (this.form.value.type) {
      case AlertRuleType.ErrorRate: return 'Error Rate % threshold';
      case AlertRuleType.SlowTrace: return 'Duration threshold (ms)';
      case AlertRuleType.LogSeveritySpike: return 'Min severity number';
      default: return 'Threshold';
    }
  };

  ngOnInit(): void {
    if (this.data.rule) {
      const r = this.data.rule;
      const cond = parseCondition(r) as (MetricThresholdCondition & ThresholdCondition) | null;
      this.form.patchValue({
        name: r.name,
        type: r.type,
        serviceName: r.serviceName ?? '',
        metricName: (cond as MetricThresholdCondition)?.metricName ?? '',
        operator: (cond as MetricThresholdCondition)?.operator ?? 'gt',
        threshold: cond?.threshold ?? 0,
        cooldownMinutes: r.cooldownMinutes,
        webhookUrl: r.webhookUrl ?? '',
        enabled: r.enabled,
      });
    }
  }

  protected save(): void {
    if (this.form.invalid) return;
    const v = this.form.value;

    let conditionJson: string;
    if (v.type === AlertRuleType.MetricThreshold) {
      conditionJson = JSON.stringify({ metricName: v.metricName, operator: v.operator, threshold: v.threshold });
    } else {
      conditionJson = JSON.stringify({ threshold: v.threshold });
    }

    const result: Partial<AlertRule> = {
      name: v.name!,
      type: v.type!,
      serviceName: v.serviceName || null,
      conditionJson,
      webhookUrl: v.webhookUrl ?? '',
      cooldownMinutes: v.cooldownMinutes ?? 30,
      enabled: v.enabled ?? true,
    };

    this.dialogRef.close(result);
  }
}
