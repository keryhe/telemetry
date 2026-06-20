import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { forkJoin } from 'rxjs';

import { AlertsApiService } from '../../core/services/api/alerts-api.service';
import {
  AlertRule, AlertEvent, ALERT_RULE_TYPE_LABELS, AlertRuleType,
  parseCondition, MetricThresholdCondition,
} from '../../core/models/alert.models';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { AlertRuleDialogComponent, AlertRuleDialogData } from './alert-rule-dialog/alert-rule-dialog.component';

@Component({
  selector: 'app-alerts',
  standalone: true,
  imports: [
    DatePipe,
    MatCardModule, MatTableModule, MatButtonModule, MatIconModule,
    MatSlideToggleModule, MatProgressBarModule, MatTooltipModule,
    EmptyStateComponent,
  ],
  templateUrl: './alerts.component.html',
  styleUrl: './alerts.component.scss',
})
export class AlertsComponent implements OnInit {
  private readonly api = inject(AlertsApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  protected loading = signal(true);
  protected rules = signal<AlertRule[]>([]);
  protected events = signal<AlertEvent[]>([]);

  protected readonly ruleTypeLabel = (type: number): string => ALERT_RULE_TYPE_LABELS[type] ?? 'Unknown';
  protected readonly ruleCols = ['name', 'type', 'service', 'condition', 'enabled', 'actions'];
  protected readonly eventCols = ['firedAt', 'ruleName', 'details'];

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    forkJoin({
      rules: this.api.getRules(),
      events: this.api.getEvents(),
    }).subscribe({
      next: ({ rules, events }) => {
        this.rules.set(rules);
        this.events.set(events);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected openCreate(): void {
    const ref = this.dialog.open<AlertRuleDialogComponent, AlertRuleDialogData>(AlertRuleDialogComponent, {
      data: {},
      maxWidth: '560px',
      width: '100%',
    });
    ref.afterClosed().subscribe((result) => {
      if (!result) return;
      this.api.createRule(result as AlertRule).subscribe({
        next: () => { this.snack.open('Rule created', undefined, { duration: 3000 }); this.load(); },
        error: () => this.snack.open('Failed to create rule', undefined, { duration: 3000 }),
      });
    });
  }

  protected openEdit(rule: AlertRule): void {
    const ref = this.dialog.open<AlertRuleDialogComponent, AlertRuleDialogData>(AlertRuleDialogComponent, {
      data: { rule },
      maxWidth: '560px',
      width: '100%',
    });
    ref.afterClosed().subscribe((result) => {
      if (!result) return;
      this.api.updateRule(rule.id, { ...rule, ...result }).subscribe({
        next: () => { this.snack.open('Rule updated', undefined, { duration: 3000 }); this.load(); },
        error: () => this.snack.open('Failed to update rule', undefined, { duration: 3000 }),
      });
    });
  }

  protected deleteRule(rule: AlertRule): void {
    if (!confirm(`Delete rule "${rule.name}"?`)) return;
    this.api.deleteRule(rule.id).subscribe({
      next: () => { this.snack.open('Rule deleted', undefined, { duration: 3000 }); this.load(); },
      error: () => this.snack.open('Failed to delete rule', undefined, { duration: 3000 }),
    });
  }

  protected toggleEnabled(rule: AlertRule): void {
    this.api.updateRule(rule.id, { ...rule, enabled: !rule.enabled }).subscribe({
      next: () => this.load(),
    });
  }

  protected conditionSummary(rule: AlertRule): string {
    try {
      const cond = parseCondition(rule) as MetricThresholdCondition & { threshold?: number };
      if (rule.type === AlertRuleType.MetricThreshold && cond?.metricName) {
        return `${cond.metricName} ${cond.operator ?? '>'} ${cond.threshold}`;
      }
      if (cond?.threshold != null) return `threshold: ${cond.threshold}`;
    } catch { /* */ }
    return rule.conditionJson || '—';
  }

  protected eventRuleName(event: AlertEvent): string {
    return event.rule?.name ?? `Rule #${event.ruleId}`;
  }

  protected eventDetails(event: AlertEvent): string {
    try {
      const parsed = JSON.parse(event.detailsJson);
      return typeof parsed === 'object' ? JSON.stringify(parsed) : String(parsed);
    } catch {
      return event.detailsJson;
    }
  }
}
