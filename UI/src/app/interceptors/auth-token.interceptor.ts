import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { from, switchMap } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';

export const authTokenInterceptor: HttpInterceptorFn = (req, next) => {
  const runtimeClientConfig = inject(RuntimeClientConfigService);
  const auth = inject(AuthService);
  const authentication = runtimeClientConfig.authentication;

  if (!authentication.enabled ||
    isClientConfigRequest(req.url) ||
    !isWorkflowApiRequest(req.url, runtimeClientConfig.apiBaseUrl)) {
    return next(req);
  }

  return from(auth.getAccessToken()).pipe(
    switchMap((token) => {
      if (!token) {
        return next(req);
      }

      return next(req.clone({
        setHeaders: {
          Authorization: `Bearer ${token}`,
        },
      }));
    }),
  );
};

function isClientConfigRequest(url: string): boolean {
  try {
    return new URL(url, window.location.origin).pathname.endsWith('/api/client-config');
  } catch {
    return url.endsWith('/api/client-config');
  }
}

function isWorkflowApiRequest(url: string, apiBaseUrl: string): boolean {
  if (!/^(http|https):\/\//i.test(url)) {
    return true;
  }

  const normalizedApiBase = apiBaseUrl.replace(/\/+$/, '');
  return url === normalizedApiBase || url.startsWith(`${normalizedApiBase}/`);
}
