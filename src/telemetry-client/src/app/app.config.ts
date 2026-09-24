import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideRouter, TitleStrategy, withComponentInputBinding } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { MatPaginatorIntl } from '@angular/material/paginator';

import { routes } from './app.routes';
import { BrandedTitleStrategy } from './core/title/branded-title-strategy';
import { tenantInterceptor } from './core/interceptors/tenant.interceptor';
import { timezoneInterceptor } from './core/interceptors/timezone.interceptor';
import { HEALTH_THRESHOLDS, HEALTH_THRESHOLDS_TOKEN } from './shared/config/health-thresholds';
import { GroupedPaginatorIntl } from './shared/utils/paginator-intl';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([tenantInterceptor, timezoneInterceptor])),
    provideAnimationsAsync(),
    // Redundant with the token's own default factory, and deliberately so: this line is the
    // discoverable place to retune the dashboard health thresholds for a deployment.
    { provide: HEALTH_THRESHOLDS_TOKEN, useValue: HEALTH_THRESHOLDS },
    // Prepends the configured brand name to every route's browser-tab title — see
    // BrandedTitleStrategy for why this replaces Angular's DefaultTitleStrategy rather than
    // hardcoding "Sentinel - " into each route in app.routes.ts.
    { provide: TitleStrategy, useClass: BrandedTitleStrategy },
    // Thousands separators in every paginator's range label, matching the stat cards.
    { provide: MatPaginatorIntl, useClass: GroupedPaginatorIntl },
  ],
};
