import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { APP_CONFIG } from './app/core/config/app-config';
import { loadAppConfig } from './app/core/config/load-config';
import {
  HEALTH_THRESHOLDS, HEALTH_THRESHOLDS_TOKEN, resolveHealthThresholds,
} from './app/shared/config/health-thresholds';

// Deployment config is fetched before bootstrap rather than in an app initializer so that
// APP_CONFIG is already a settled value when the first `inject(APP_CONFIG)` runs. The seven
// *ApiService classes read it in a field initializer, so an initializer-based load would be a
// race: any service constructed during bootstrap would capture the default and keep it.
loadAppConfig()
  .then((config) => {
    // Narrows the window where the tab shows index.html's generic static <title> instead of the
    // configured brand: BrandedTitleStrategy takes over from here on the first route change, but
    // that is itself a moment after bootstrap, not before it.
    document.title = config.brandName;

    // `oidc` mode only: the sign-in client is imported on demand so a cookie-mode deployment never
    // downloads it. Its initializer holds bootstrap until the user is signed in.
    const oidcProviders = config.auth.mode === 'oidc'
      ? import('./app/core/auth/oidc-providers').then((m) => m.provideOidcAuth(config))
      : Promise.resolve([]);

    return oidcProviders.then((oidc) => bootstrapApplication(App, {
      ...appConfig,
      providers: [
        ...appConfig.providers,
        ...oidc,
        { provide: APP_CONFIG, useValue: config },
        // The built-in thresholds with whatever the host configured merged over them.
        {
          provide: HEALTH_THRESHOLDS_TOKEN,
          useValue: resolveHealthThresholds(config.healthThresholds, HEALTH_THRESHOLDS),
        },
      ],
    }));
  })
  .catch((err) => console.error(err));
