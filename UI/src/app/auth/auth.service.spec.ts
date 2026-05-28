import '@angular/compiler';
import {
  EnvironmentInjector,
  createEnvironmentInjector,
  runInInjectionContext,
} from '@angular/core';
import { Router } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';
import { AuthService } from './auth.service';

describe('AuthService', () => {
  let injector: EnvironmentInjector | undefined;

  afterEach(() => {
    injector?.destroy();
    injector = undefined;
  });

  it('treats disabled auth mode as authenticated without redirecting', async () => {
    const router = {
      url: '/dashboard',
      navigateByUrl: vi.fn(),
    };
    injector = createEnvironmentInjector([
      {
        provide: RuntimeClientConfigService,
        useValue: {
          authentication: {
            enabled: false,
            authority: '',
            clientId: '',
            scope: '',
            audience: '',
          },
        },
      },
      { provide: AuthService, useClass: AuthService },
      { provide: Router, useValue: router },
    ], null as never);

    const service = runInInjectionContext(injector, () => injector!.get(AuthService));

    await service.initialize();
    const ensured = await service.ensureAuthenticated('/blueprints');
    const callbackReturnUrl = await service.completeLoginCallback();

    expect(ensured).toBe(true);
    expect(service.isAuthenticated()).toBe(true);
    expect(service.userProfile()?.displayName).toBe('Local Development');
    expect(await service.getAccessToken()).toBeNull();
    expect(callbackReturnUrl).toBe('/dashboard');
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });
});
