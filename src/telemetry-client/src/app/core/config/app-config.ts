import { InjectionToken } from '@angular/core';

/**
 * Deployment-specific settings the UI reads at startup rather than at build time.
 *
 * The distinction matters because the compiled bundle ships as a NuGet package
 * (`Keryhe.Telemetry.Ui`) that any host can serve — see plans/ui-packaging-runtime-config.md.
 * Anything baked in here at build time is, by definition, not configurable by the people who
 * consume that package, since not having to rebuild the SPA is the whole point of shipping it
 * prebuilt. That is why this replaced the former `src/environments/environment*.ts` pair and the
 * `fileReplacements` swap in angular.json.
 *
 * Loaded by {@link loadAppConfig} before `bootstrapApplication`, so the value is a genuine
 * constant by the time anything injects it.
 */
export interface AppConfig {
  /**
   * Base URL of the telemetry REST API, with no trailing slash. Either same-origin
   * (`/api`, the default — the host serves the SPA and the API together) or absolute
   * (`https://telemetry.example.com/api`, when the two are deployed separately, which
   * requires the API host's CORS policy to name the UI's origin).
   */
  apiUrl: string;

  /**
   * The product name shown in the header bar and, via {@link BrandedTitleStrategy}, in every
   * route's browser tab title ("<brandName> - Traces"). Baked-in text is exactly the "can't be
   * configured without a rebuild" problem `apiUrl` above solves, so it lives in the same
   * runtime config rather than the template.
   */
  brandName: string;

  /** The tagline shown under {@link brandName} in the header bar. */
  brandTagline: string;
}

/**
 * Defaults, used when no `config.json` is served or it cannot be parsed — also this UI's own
 * out-of-the-box branding, so a host that sets nothing sees exactly what it always has.
 */
export const DEFAULT_APP_CONFIG: AppConfig = {
  apiUrl: '/api',
  brandName: 'Sentinel',
  brandTagline: 'OpenTelemetry Visualization',
};

/**
 * Injected by the eight `*ApiService` classes to build their base URLs.
 *
 * Carries a default factory *and* is explicitly provided in `main.ts`, the same belt-and-braces
 * arrangement as `HEALTH_THRESHOLDS_TOKEN`: the factory keeps the token usable in tests and in
 * any bootstrap path that skips the loader, while the explicit provider is the discoverable
 * place to see where the real value comes from.
 */
export const APP_CONFIG = new InjectionToken<AppConfig>('APP_CONFIG', {
  providedIn: 'root',
  factory: () => DEFAULT_APP_CONFIG,
});
