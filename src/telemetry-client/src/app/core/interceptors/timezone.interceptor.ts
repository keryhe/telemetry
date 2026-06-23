import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { TimezoneService } from '../services/timezone.service';

/** Attaches the browser timezone so the server can localize UTC timestamps. */
export const timezoneInterceptor: HttpInterceptorFn = (req, next) => {
  const tz = inject(TimezoneService).timeZone;
  return next(req.clone({ headers: req.headers.set('X-Timezone', tz) }));
};
