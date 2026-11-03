import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LogEventStream } from './log-event-stream';

describe('LogEventStream', () => {
  let stream: LogEventStream | undefined;

  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    stream?.close();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('reads split UTF-8, CRLF and multiline SSE batches without emitting keepalives', async () => {
    let bodyController!: ReadableStreamDefaultController<Uint8Array>;
    const body = new ReadableStream<Uint8Array>({ start: (controller) => { bodyController = controller; } });
    vi.stubGlobal('fetch', vi.fn(async () => new Response(body)));
    stream = new LogEventStream(new URL('http://api.local/logs/stream'), async () => 'token');
    const batches: string[] = [];
    stream.addEventListener('logBatch', (event) => batches.push((event as MessageEvent<string>).data));
    await settle();

    const frame = new TextEncoder().encode(': keepalive\r\n\r\nevent: logBatch\r\ndata: {"entries":[{"message":"café"}],\r\ndata: "nextCursor":"123"}\r\n\r\n');
    // Each byte is a separate network chunk, including within a multibyte character and CRLF.
    for (const byte of frame) {
      bodyController.enqueue(new Uint8Array([byte]));
    }
    await settle(frame.length * 2);

    expect(batches).toHaveLength(1);
    expect(JSON.parse(batches[0])).toEqual({ entries: [{ message: 'café' }], nextCursor: '123' });
    stream.close();
    bodyController.close();
  });

  it('reconnects using the latest token and last delivered batch cursor', async () => {
    const bodies: ReadableStreamDefaultController<Uint8Array>[] = [];
    const fetchMock = vi.fn(async () => new Response(new ReadableStream<Uint8Array>({
      start: (controller) => { bodies.push(controller); },
    })));
    vi.stubGlobal('fetch', fetchMock);
    const getAccessToken = vi.fn()
      .mockResolvedValueOnce('original-token')
      .mockResolvedValue('renewed-token');
    stream = new LogEventStream(new URL('http://api.local/logs/stream?from=start&cursor=100'), getAccessToken);
    const errors = vi.fn();
    stream.onerror = errors;
    await settle();
    bodies[0].enqueue(new TextEncoder().encode('event: logBatch\ndata: {"entries":[],"nextCursor":"200"}\n\n'));
    bodies[0].close();
    await settle();
    expect(errors).toHaveBeenCalledOnce();

    await vi.advanceTimersByTimeAsync(1000);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    const [url, options] = fetchMock.mock.calls[1] as unknown as [URL, RequestInit];
    expect(url.searchParams.get('cursor')).toBe('200');
    expect(url.searchParams.get('from')).toBe('start');
    expect(url.toString()).not.toContain('token');
    expect(options.headers).toMatchObject({ Authorization: 'Bearer renewed-token' });
    expect(getAccessToken).toHaveBeenCalledTimes(2);
    stream.close();
    bodies[1].close();
  });

  it('retries authentication failures with a fresh token', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(new Response(new ReadableStream<Uint8Array>()));
    vi.stubGlobal('fetch', fetchMock);
    const getAccessToken = vi.fn()
      .mockResolvedValueOnce('expired-token')
      .mockResolvedValueOnce('current-token');
    stream = new LogEventStream(new URL('http://api.local/logs/stream'), getAccessToken);
    await settle();

    await vi.advanceTimersByTimeAsync(1000);

    expect(fetchMock.mock.calls[1][1].headers.Authorization).toBe('Bearer current-token');
  });

  it('aborts an active fetch and suppresses reconnects when closed', async () => {
    let bodyController!: ReadableStreamDefaultController<Uint8Array>;
    const fetchMock = vi.fn(async () => new Response(new ReadableStream<Uint8Array>({
      start: (controller) => { bodyController = controller; },
    })));
    vi.stubGlobal('fetch', fetchMock);
    stream = new LogEventStream(new URL('http://api.local/logs/stream'), async () => null);
    const errors = vi.fn();
    stream.onerror = errors;
    await settle();

    stream.close();
    bodyController.error(new DOMException('Aborted', 'AbortError'));
    await vi.advanceTimersByTimeAsync(20000);

    const [, options] = fetchMock.mock.calls[0] as unknown as [URL, RequestInit];
    expect(options.signal?.aborted).toBe(true);
    expect(errors).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it('cancels a pending reconnect when closed', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 503 }));
    vi.stubGlobal('fetch', fetchMock);
    stream = new LogEventStream(new URL('http://api.local/logs/stream'), async () => null);
    await settle();

    stream.close();
    await vi.advanceTimersByTimeAsync(20000);

    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it('does not start fetching after closing while token resolution is pending', async () => {
    let resolveToken!: (token: string) => void;
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    stream = new LogEventStream(new URL('http://api.local/logs/stream'), () => new Promise((resolve) => { resolveToken = resolve; }));

    stream.close();
    resolveToken('token');
    await settle();

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('honors close from an error listener before scheduling another attempt', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 503 })));
    stream = new LogEventStream(new URL('http://api.local/logs/stream'), async () => null);
    stream.onerror = () => stream!.close();

    await settle();

    expect(vi.getTimerCount()).toBe(0);
  });

  async function settle(turns = 20): Promise<void> {
    for (let index = 0; index < turns; index++) {
      await Promise.resolve();
    }
  }
});
