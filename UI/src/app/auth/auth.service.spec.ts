import '@angular/compiler';
import {
  EnvironmentInjector,
  createEnvironmentInjector,
  runInInjectionContext,
} from '@angular/core';
import { Router } from '@angular/router';
import { UserManager } from 'oidc-client-ts';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';
import { AuthService } from './auth.service';

const oidc = vi.hoisted(() => ({
  getUser: vi.fn(),
  removeUser: vi.fn(),
  signinRedirect: vi.fn(),
  signinRedirectCallback: vi.fn(),
}));

vi.mock('oidc-client-ts', () => ({
  UserManager: vi.fn(function () { return oidc; }),
  WebStorageStateStore: vi.fn(),
}));

describe('AuthService', () => {
  let injector: EnvironmentInjector | undefined;

  beforeEach(() => {
    vi.clearAllMocks();
    oidc.getUser.mockResolvedValue(null);
    oidc.removeUser.mockResolvedValue(undefined);
    oidc.signinRedirect.mockReset();
    oidc.signinRedirectCallback.mockReset();
  });

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

  it('bounds OIDC discovery and other requests with a finite timeout', async () => {
    const service = createAuthenticatedService();

    await service.initialize();

    expect(UserManager).toHaveBeenCalledWith(expect.objectContaining({
      authority: 'https://identity.local/realms/workflow-builder',
      requestTimeoutInSeconds: 10,
    }));
  });

  it('rejects protected navigation with a recoverable message when discovery fails', async () => {
    oidc.signinRedirect.mockRejectedValue(new TypeError('Failed to fetch OIDC metadata'));
    const service = createAuthenticatedService();

    await expect(service.ensureAuthenticated('/processes?processId=123')).resolves.toBe(false);

    expect(service.isAuthenticated()).toBe(false);
    expect(service.signInError()).toBe('Sign in is temporarily unavailable. Please try again.');
    expect(oidc.signinRedirect).toHaveBeenCalledWith({ state: { returnUrl: '/processes?processId=123' } });
  });

  it('clears discovery failure on retry and preserves the intended protected route', async () => {
    oidc.signinRedirect.mockRejectedValueOnce(new Error('Discovery timed out'));
    const service = createAuthenticatedService();
    await service.ensureAuthenticated('/phases');
    let finishRedirect!: () => void;
    oidc.signinRedirect.mockImplementationOnce(() => new Promise<void>((resolve) => { finishRedirect = resolve; }));

    const retry = service.login();

    expect(service.signInError()).toBeNull();
    expect(oidc.signinRedirect).toHaveBeenLastCalledWith({ state: { returnUrl: '/phases' } });
    finishRedirect();
    await retry;
    // Starting a redirect does not grant access before its authenticated callback.
    expect(service.isAuthenticated()).toBe(false);
  });

  it('clears the failure and authenticates only after a successful sign-in callback', async () => {
    oidc.signinRedirect.mockRejectedValueOnce(new Error('Discovery unavailable'));
    const service = createAuthenticatedService();
    await service.login('/blueprints');
    oidc.signinRedirectCallback.mockResolvedValue({
      profile: { sub: 'user-123', preferred_username: 'Local User' },
      state: { returnUrl: '/blueprints' },
    });

    await expect(service.completeLoginCallback()).resolves.toBe('/blueprints');

    expect(service.signInError()).toBeNull();
    expect(service.isAuthenticated()).toBe(true);
    expect(service.userProfile()?.displayName).toBe('Local User');
  });

  function createAuthenticatedService(): AuthService {
    injector = createEnvironmentInjector([
      {
        provide: RuntimeClientConfigService,
        useValue: {
          authentication: {
            enabled: true,
            authority: 'https://identity.local/realms/workflow-builder',
            clientId: 'workflow-builder-spa',
            scope: 'openid profile email',
            audience: 'workflow-builder-api',
          },
        },
      },
      { provide: AuthService, useClass: AuthService },
      { provide: Router, useValue: { url: '/', navigateByUrl: vi.fn() } },
    ], null as never);
    return runInInjectionContext(injector, () => injector!.get(AuthService));
  }
});
