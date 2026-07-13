import { afterEach, describe, expect, it, vi } from 'vitest';
import { RuntimeClientConfigService } from './runtime-client-config.service';

describe('RuntimeClientConfigService', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('loads client config from the bootstrapped API origin', async () => {
    const service = new RuntimeClientConfigService();
    const fetch = mockConfigRequests('https://api.workflow.test', {
      apiBaseUrl: 'https://api.workflow.test',
      signalRBaseUrl: 'https://signalr.workflow.test',
      grafanaBaseUrl: 'https://grafana.workflow.test',
      authentication: {
        enabled: true,
        authority: 'https://identity.workflow.test/realms/workflow-builder',
        clientId: 'workflow-builder-spa',
        scope: 'openid profile email',
        audience: 'workflow-builder-api',
      },
    });

    await service.load();

    expect(fetch).toHaveBeenNthCalledWith(1, '/runtime-bootstrap.json', {
      credentials: 'omit',
      cache: 'no-store',
    });
    expect(fetch).toHaveBeenNthCalledWith(2, 'https://api.workflow.test/api/client-config', {
      credentials: 'omit',
      cache: 'no-store',
    });
    expect(service.apiBaseUrl).toBe('https://api.workflow.test');
    expect(service.signalRBaseUrl).toBe('https://signalr.workflow.test');
    expect(service.grafanaBaseUrl).toBe('https://grafana.workflow.test');
    expect(service.authentication).toEqual({
      enabled: true,
      authority: 'https://identity.workflow.test/realms/workflow-builder',
      clientId: 'workflow-builder-spa',
      scope: 'openid profile email',
      audience: 'workflow-builder-api',
    });
  });

  it('uses the same-origin API proxy when the bootstrap origin is empty', async () => {
    const service = new RuntimeClientConfigService();
    const fetch = mockConfigRequests('', {
      apiBaseUrl: 'http://localhost:5180/',
      signalRBaseUrl: 'http://localhost:64217/',
      grafanaBaseUrl: 'http://localhost:3000/',
      authentication: {
        enabled: true,
        authority: 'https://localhost:8080/realms/workflow-builder/',
        clientId: 'workflow-builder-spa',
        scope: 'openid profile email',
      },
    });

    await service.load();

    expect(fetch).toHaveBeenNthCalledWith(2, '/api/client-config', {
      credentials: 'omit',
      cache: 'no-store',
    });
    expect(service.apiBaseUrl).toBe('http://localhost:5180');
    expect(service.signalRBaseUrl).toBe('http://localhost:64217');
    expect(service.grafanaBaseUrl).toBe('http://localhost:3000');
    expect(service.authentication.authority).toBe('https://localhost:8080/realms/workflow-builder');
  });

  it('defaults missing authentication settings to disabled mode', async () => {
    const service = new RuntimeClientConfigService();
    mockConfigRequests('', {
      apiBaseUrl: 'http://localhost:5180',
      signalRBaseUrl: 'http://localhost:64217',
      grafanaBaseUrl: 'http://localhost:3000',
    });

    await service.load();

    expect(service.authentication).toEqual({
      enabled: false,
      authority: '',
      clientId: '',
      scope: '',
      audience: '',
    });
  });
});

function mockConfigRequests(apiBaseUrl: string, clientConfig: object) {
  return vi.spyOn(globalThis, 'fetch')
    .mockResolvedValueOnce(jsonResponse({ apiBaseUrl }))
    .mockResolvedValueOnce(jsonResponse(clientConfig));
}

function jsonResponse(value: object): Response {
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}
