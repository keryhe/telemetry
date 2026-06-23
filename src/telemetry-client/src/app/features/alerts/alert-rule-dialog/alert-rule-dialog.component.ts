import { Component, OnInit, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';

import { AlertRule, AlertRuleType, ALERT_RULE_TYPE_LABELS, parseCondition } from '../../../core/models/alert.models';

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

        <!-- Metric Threshold -->
        @if (type() === AlertRuleType.MetricThreshold) {
          <mat-form-field appearance="outline" class="full-width">
            <mat-label>Metric Name</mat-label>
            <input matInput formControlName="metricName" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Operator</mat-label>
            <mat-select formControlName="operator">
              <mat-option value=">">&gt; (greater than)</mat-option>
              <mat-option value="<">&lt; (less than)</mat-option>
              <mat-option value=">=">&ge; (greater or equal)</mat-option>
              <mat-option value="<=">&le; (less or equal)</mat-option>
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Threshold</mat-label>
            <input matInput type="number" formControlName="threshold" />
          </mat-form-field>
        }

        <!-- Error Rate -->
        @if (type() === AlertRuleType.ErrorRate) {
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Error Rate % threshold</mat-label>
            <input matInput type="number" formControlName="thresholdPercent" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Window (minutes)</mat-label>
            <input matInput type="number" formControlName="windowMinutes" />
          </mat-form-field>
        }

        <!-- Slow Trace -->
        @if (type() === AlertRuleType.SlowTrace) {
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Duration threshold (ms)</mat-label>
            <input matInput type="number" formControlName="minDurationMs" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Window (minutes)</mat-label>
            <input matInput type="number" formControlName="windowMinutes" />
          </mat-form-field>
        }

        <!-- Log Severity Spike -->
        @if (type() === AlertRuleType.LogSeveritySpike) {
          <mat-form-field appearance="outline" class="full-width">
            <mat-label>Minimum severity</mat-label>
            <mat-select formControlName="minSeverity">
              <mat-option [value]="5">Debug</mat-option>
              <mat-option [value]="9">Info</mat-option>
              <mat-option [value]="13">Warn</mat-option>
              <mat-option [value]="17">Error</mat-option>
              <mat-option [value]="21">Fatal</mat-option>
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Count threshold</mat-label>
            <input matInput type="number" formControlName="countThreshold" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="half-width">
            <mat-label>Window (minutes)</mat-label>
            <input matInput type="number" formControlName="windowMinutes" />
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
    .dialog-form { display: flex; flex-direction: column; flex-wrap: wrap; gap: 4px; padding-top: 4px; min-width: 480px; }
    .full-width { width: 100%; }
    .half-width { width: calc(50% - 4px); }
  `],
})
export class AlertRuleDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  readonly dialogRef = inject(MatDialogRef<AlertRuleDialogComponent>);
  readonly data: AlertRuleDialogData = inject(MAT_DIALOG_DATA);

  protected readonly AlertRuleType = AlertRuleType;

  protected form = this.fb.group({
    name: ['', Validators.required],
    type: [AlertRuleType.MetricThreshold, Validators.required],
    serviceName: [''],
    // Metric Threshold
    metricName: [''],
    operator: ['>'],
    threshold: [0, Validators.min(0)],
    // Error Rate
    thresholdPercent: [5, Validators.min(0)],
    // Slow Trace
    minDurationMs: [1000, Validators.min(1)],
    // Log Severity Spike
    minSeverity: [17],
    countThreshold: [10, Validators.min(1)],
    // Shared window (ErrorRate / SlowTrace / LogSeveritySpike)
    windowMinutes: [5, Validators.min(1)],
    // Common
    cooldownMinutes: [30, Validators.min(1)],
    webhookUrl: [''],
    enabled: [true],
  });

  protected readonly ruleTypes = Object.entries(ALERT_RULE_TYPE_LABELS).map(([v, label]) => ({
    value: Number(v), label,
  }));

  protected type = (): AlertRuleType => this.form.value.type ?? AlertRuleType.MetricThreshold;

  ngOnInit(): void {
    if (!this.data.rule) return;
    const r = this.data.rule;
    const cond = (parseCondition(r) ?? {}) as Record<string, unknown>;
    this.form.patchValue({
      name: r.name,
      type: r.type,
      serviceName: r.serviceName ?? '',
      cooldownMinutes: r.cooldownMinutes,
      webhookUrl: r.webhookUrl ?? '',
      enabled: r.enabled,
      // condition fields (PascalCase from stored JSON)
      metricName: (cond['MetricName'] as string) ?? '',
      operator: (cond['Operator'] as string) ?? '>',
      threshold: Number(cond['Threshold'] ?? 0),
      thresholdPercent: Number(cond['ThresholdPercent'] ?? 5),
      minDurationMs: Number(cond['MinDurationMs'] ?? 1000),
      minSeverity: Number(cond['MinSeverity'] ?? 17),
      countThreshold: Number(cond['CountThreshold'] ?? 10),
      windowMinutes: Number(cond['WindowMinutes'] ?? 5),
    });
  }

  protected save(): void {
    if (this.form.invalid) return;
    const v = this.form.value;

    let condition: Record<string, unknown>;
    switch (v.type) {
      case AlertRuleType.MetricThreshold:
        condition = { MetricName: v.metricName, Operator: v.operator, Threshold: Number(v.threshold) };
        break;
      case AlertRuleType.ErrorRate:
        condition = { ThresholdPercent: Number(v.thresholdPercent), WindowMinutes: Number(v.windowMinutes) };
        break;
      case AlertRuleType.SlowTrace:
        condition = { MinDurationMs: Number(v.minDurationMs), WindowMinutes: Number(v.windowMinutes) };
        break;
      case AlertRuleType.LogSeveritySpike:
        condition = {
          MinSeverity: Number(v.minSeverity),
          CountThreshold: Number(v.countThreshold),
          WindowMinutes: Number(v.windowMinutes),
        };
        break;
      default:
        condition = {};
    }

    const result: Partial<AlertRule> = {
      name: v.name!,
      type: v.type!,
      serviceName: v.serviceName || null,
      conditionJson: JSON.stringify(condition),
      webhookUrl: v.webhookUrl ?? '',
      cooldownMinutes: v.cooldownMinutes ?? 30,
      enabled: v.enabled ?? true,
    };

    this.dialogRef.close(result);
  }
}
