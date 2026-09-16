import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';

import { routes } from './app.routes';
import { tenantInterceptor } from './core/interceptors/tenant.interceptor';
import { timezoneInterceptor } from './core/interceptors/timezone.interceptor';
import { HEALTH_THRESHOLDS, HEALTH_THRESHOLDS_TOKEN } from './shared/config/health-thresholds';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([tenantInterceptor, timezoneInterceptor])),
    provideAnimationsAsync(),
    // Redundant with the token's own default factory, and deliberately so: this line is the
    // discoverable place to retune the dashboard health thresholds for a deployment.
    { provide: HEALTH_THRESHOLDS_TOKEN, useValue: HEALTH_THRESHOLDS },
  ],
};
