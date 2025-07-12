// src/app/interceptors/api-prefix.interceptor.ts
import {
  HttpInterceptorFn
} from '@angular/common/http';
import { environment } from '../../environments/environment';

export const apiPrefixInterceptor: HttpInterceptorFn = (req, next) => {
  const apiBaseUrl = environment.apiBaseUrl;

  // Skip absolute URLs
  if (/^(http|https):\/\//i.test(req.url)) {
    return next(req);
  }

  const modifiedReq = req.clone({
    url: `${apiBaseUrl}/${req.url}`.replace(/([^:]\/)\/+/g, '$1')
  });

  return next(modifiedReq);
};
