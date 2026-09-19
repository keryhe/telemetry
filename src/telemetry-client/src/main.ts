import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { APP_CONFIG } from './app/core/config/app-config';
import { loadAppConfig } from './app/core/config/load-config';

// Deployment config is fetched before bootstrap rather than in an app initializer so that
// APP_CONFIG is already a settled value when the first `inject(APP_CONFIG)` runs. The eight
// *ApiService classes read it in a field initializer, so an initializer-based load would be a
// race: any service constructed during bootstrap would capture the default and keep it.
loadAppConfig()
  .then((config) => {
    // Narrows the window where the tab shows index.html's generic static <title> instead of the
    // configured brand: BrandedTitleStrategy takes over from here on the first route change, but
    // that is itself a moment after bootstrap, not before it.
    document.title = config.brandName;

    return bootstrapApplication(App, {
      ...appConfig,
      providers: [...appConfig.providers, { provide: APP_CONFIG, useValue: config }],
    });
  })
  .catch((err) => console.error(err));
