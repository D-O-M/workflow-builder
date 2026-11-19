// src/app/interceptors/api-prefix.interceptor.ts
import {
  HttpInterceptorFn
} from '@angular/common/http';
import { inject } from '@angular/core';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';

export const apiPrefixInterceptor: HttpInterceptorFn = (req, next) => {
  const apiBaseUrl = inject(RuntimeClientConfigService).apiBaseUrl;

  // Skip absolute URLs
  if (/^(?:https?:)?\/\//i.test(req.url)) {
    return next(req);
  }

  const modifiedReq = req.clone({
    url: `${apiBaseUrl.replace(/\/+$/, '')}/${req.url.replace(/^\/+/, '')}`
  });

  return next(modifiedReq);
};
