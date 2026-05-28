import '@angular/compiler';
import {
  EnvironmentInjector,
  createEnvironmentInjector,
  runInInjectionContext,
} from '@angular/core';
import { HttpRequest, HttpResponse } from '@angular/common/http';
import { firstValueFrom, of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from '../auth/auth.service';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';
import { authTokenInterceptor } from './auth-token.interceptor';

describe('authTokenInterceptor', () => {
  let injector: EnvironmentInjector | undefined;

  afterEach(() => {
    injector?.destroy();
    injector = undefined;
  });

  it('attaches bearer tokens to workflow API requests', async () => {
    const captured = await intercept('http://api.local/api/blueprints', true, 'token-123');

    expect(captured.headers.get('Authorization')).toBe('Bearer token-123');
  });

  it('does not attach bearer tokens to unrelated absolute URLs', async () => {
    const captured = await intercept('https://example.test/api/blueprints', true, 'token-123');

    expect(captured.headers.has('Authorization')).toBe(false);
  });

  it('does not attach bearer tokens when authentication is disabled', async () => {
    const captured = await intercept('http://api.local/api/blueprints', false, 'token-123');

    expect(captured.headers.has('Authorization')).toBe(false);
  });

  async function intercept(
    url: string,
    authenticationEnabled: boolean,
    accessToken: string | null,
  ): Promise<HttpRequest<unknown>> {
    let captured: HttpRequest<unknown> | null = null;
    injector = createEnvironmentInjector([
      {
        provide: RuntimeClientConfigService,
        useValue: {
          apiBaseUrl: 'http://api.local',
          authentication: {
            enabled: authenticationEnabled,
          },
        },
      },
      {
        provide: AuthService,
        useValue: {
          getAccessToken: vi.fn(async () => accessToken),
        },
      },
    ], null as never);

    await runInInjectionContext(injector, async () => {
      await firstValueFrom(authTokenInterceptor(
        new HttpRequest('GET', url),
        (request) => {
          captured = request;
          return of(new HttpResponse({ status: 200 }));
        },
      ));
    });

    return captured!;
  }
});
