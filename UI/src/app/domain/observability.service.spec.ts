import '@angular/compiler';
import { EnvironmentInjector, createEnvironmentInjector, runInInjectionContext } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from '../auth/auth.service';
import { LogEventStream } from './log-event-stream';
import { ObservabilityService } from './observability.service';
import { RuntimeClientConfigService } from './runtime-client-config.service';

describe('ObservabilityService log streaming', () => {
  let injector: EnvironmentInjector | undefined;
  let stream: LogEventStream | undefined;

  afterEach(() => {
    stream?.close();
    injector?.destroy();
    vi.unstubAllGlobals();
  });

  it.each([
    ['process', '/api/observability/processes/process-id/logs/stream'],
    ['runtime-step', '/api/observability/processes/process-id/steps/step-id/logs/stream'],
    ['blueprint-step', '/api/blueprints/3/processes/process-id/steps/node-id/logs/stream'],
  ])('authenticates the %s stream through headers', async (kind, expectedPath) => {
    const { service, fetchMock, getAccessToken } = createService(true, 'access-token');
    const options = { from: '2026-10-20T09:00:00Z', cursor: '123' };
    stream = kind === 'process'
      ? service.createProcessLogStream('process-id', options)
      : kind === 'runtime-step'
        ? service.createProcessStepLogStreamByRuntimeId('process-id', 'step-id', options)
        : service.createProcessStepLogStream(3, 'process-id', 'node-id', options);
    await settle();

    const [url, request] = fetchMock.mock.calls[0] as unknown as [URL, RequestInit];
    expect(url.pathname).toBe(expectedPath);
    expect(url.searchParams.get('cursor')).toBe('123');
    expect(url.searchParams.get('from')).toBe(options.from);
    expect(url.toString()).not.toContain('access-token');
    expect(request.headers).toMatchObject({ Authorization: 'Bearer access-token', Accept: 'text/event-stream' });
    expect(request.credentials).toBe('omit');
    expect(getAccessToken).toHaveBeenCalledOnce();
  });

  it('streams without contacting OIDC when local authentication is disabled', async () => {
    const { service, fetchMock, getAccessToken } = createService(false, null);
    stream = service.createProcessLogStream('process-id');
    await settle();

    const [, request] = fetchMock.mock.calls[0] as unknown as [URL, RequestInit];
    expect(request.headers).not.toHaveProperty('Authorization');
    expect(getAccessToken).not.toHaveBeenCalled();
  });

  it('does not send an unauthenticated request when the required token is unavailable', async () => {
    const { service, fetchMock } = createService(true, null);
    stream = service.createProcessLogStream('process-id');
    const errors = vi.fn();
    stream.onerror = errors;
    await settle();

    expect(fetchMock).not.toHaveBeenCalled();
    expect(errors).toHaveBeenCalledOnce();
  });

  function createService(authenticationEnabled: boolean, token: string | null) {
    const getAccessToken = vi.fn(async () => token);
    const fetchMock = vi.fn(async () => new Response(new ReadableStream<Uint8Array>()));
    vi.stubGlobal('fetch', fetchMock);
    injector = createEnvironmentInjector([
      { provide: HttpClient, useValue: {} },
      { provide: RuntimeClientConfigService, useValue: { apiBaseUrl: 'http://api.local', authentication: { enabled: authenticationEnabled } } },
      { provide: AuthService, useValue: { getAccessToken } },
    ], null as never);
    const service = runInInjectionContext(injector, () => new ObservabilityService());
    return { service, fetchMock, getAccessToken };
  }

  async function settle(): Promise<void> {
    for (let index = 0; index < 20; index++) {
      await Promise.resolve();
    }
  }
});
