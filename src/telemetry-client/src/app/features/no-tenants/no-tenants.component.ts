import { Component } from '@angular/core';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';

/** Shown when the caller may access no tenant at all (or none exists yet). */
@Component({
  selector: 'app-no-tenants',
  standalone: true,
  imports: [EmptyStateComponent],
  template: `<app-empty-state icon="lock" message="No tenants available"
    hint="You do not have access to any tenant, or none has been created yet." />`,
})
export class NoTenantsComponent {}
