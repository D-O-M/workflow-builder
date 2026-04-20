import { afterEach, describe, expect, it, vi } from 'vitest';
import { RuntimeClientConfigService } from './runtime-client-config.service';

describe('RuntimeClientConfigService', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('uses the configured API and SignalR base urls from client-config', async () => {
    const service = new RuntimeClientConfigService();
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({
      apiBaseUrl: 'http://localhost:5180',
      signalRBaseUrl: 'http://localhost:64217',
      grafanaBaseUrl: 'http://localhost:3000',
    }), {
      status: 200,
      headers: {
        'Content-Type': 'application/json',
      },
    }));

    await service.load();

    expect(service.apiBaseUrl).toBe('http://localhost:5180');
    expect(service.signalRBaseUrl).toBe('http://localhost:64217');
    expect(service.grafanaBaseUrl).toBe('http://localhost:3000');
  });

  it('normalizes trailing slashes from configured urls', async () => {
    const service = new RuntimeClientConfigService();
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({
      apiBaseUrl: 'http://localhost:5180/',
      signalRBaseUrl: 'http://localhost:64217/',
      grafanaBaseUrl: 'http://localhost:3000/',
    }), {
      status: 200,
      headers: {
        'Content-Type': 'application/json',
      },
    }));

    await service.load();

    expect(service.apiBaseUrl).toBe('http://localhost:5180');
    expect(service.signalRBaseUrl).toBe('http://localhost:64217');
    expect(service.grafanaBaseUrl).toBe('http://localhost:3000');
  });
});
