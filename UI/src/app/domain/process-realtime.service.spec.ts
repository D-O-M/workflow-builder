import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import { createProcessRealtimeConnectionOptions } from './process-realtime.service';

describe('createProcessRealtimeConnectionOptions', () => {
  it('adds an access token factory when authentication is enabled', async () => {
    const options = createProcessRealtimeConnectionOptions(true, async () => 'token-123');

    expect(options.withCredentials).toBe(false);
    expect(options.accessTokenFactory).toBeDefined();
    await expect(options.accessTokenFactory!()).resolves.toBe('token-123');
  });

  it('keeps unauthenticated SignalR options when authentication is disabled', () => {
    const options = createProcessRealtimeConnectionOptions(false, async () => 'token-123');

    expect(options.withCredentials).toBe(false);
    expect(options.accessTokenFactory).toBeUndefined();
  });
});
