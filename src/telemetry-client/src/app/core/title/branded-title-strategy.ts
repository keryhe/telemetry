import { Injectable, inject } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';
import { APP_CONFIG } from '../config/app-config';

/**
 * Prepends the configured brand name to each route's own `title` ("<brandName> - Traces"), so the
 * "Sentinel - " prefix that used to be baked into every route in app.routes.ts doesn't have to be
 * — those routes now carry only their own segment ("Traces"), and this is the one place branding
 * joins in, from runtime config rather than a compiled-in string.
 *
 * Delegates to the base class's `buildTitle()` for *which* routes get a title at all, so routes
 * with no `title` of their own (`traces/:id`, `metrics/:name`) keep the previous page's title
 * unchanged on navigation — exactly Angular's own `DefaultTitleStrategy` behavior, which this
 * otherwise mirrors.
 */
@Injectable({ providedIn: 'root' })
export class BrandedTitleStrategy extends TitleStrategy {
  private readonly title = inject(Title);
  private readonly config = inject(APP_CONFIG);

  override updateTitle(snapshot: RouterStateSnapshot): void {
    const routeTitle = this.buildTitle(snapshot);
    if (routeTitle !== undefined) {
      this.title.setTitle(`${this.config.brandName} - ${routeTitle}`);
    }
  }
}
