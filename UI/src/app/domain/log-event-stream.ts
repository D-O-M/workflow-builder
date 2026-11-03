/** Fetch-based SSE keeps bearer credentials in headers and resumes log batches after reconnecting. */
export class LogEventStream extends EventTarget {
  onerror: ((event: Event) => void) | null = null;

  private closed = false;
  private controller: AbortController | null = null;
  private reconnectTimeout: ReturnType<typeof setTimeout> | null = null;
  private reconnectDelay = 1000;
  private cursor: string | null;

  constructor(
    private readonly url: URL,
    private readonly getAccessToken: () => Promise<string | null>,
  ) {
    super();
    this.cursor = url.searchParams.get('cursor');
    void this.connect();
  }

  close(): void {
    this.closed = true;
    this.controller?.abort();
    this.controller = null;
    if (this.reconnectTimeout !== null) {
      clearTimeout(this.reconnectTimeout);
      this.reconnectTimeout = null;
    }
  }

  private async connect(): Promise<void> {
    try {
      // Resolve the current token on every attempt, including after an OIDC token renewal.
      const token = await this.getAccessToken();
      if (this.closed) {
        return;
      }

      const requestUrl = new URL(this.url);
      if (this.cursor) {
        requestUrl.searchParams.set('cursor', this.cursor);
      }

      const controller = new AbortController();
      this.controller = controller;
      const response = await fetch(requestUrl, {
        headers: {
          Accept: 'text/event-stream',
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        credentials: 'omit',
        cache: 'no-store',
        signal: controller.signal,
      });

      if (!response.ok || !response.body) {
        throw new Error(`Unable to stream logs (${response.status}).`);
      }

      this.reconnectDelay = 1000;
      await this.readEvents(response.body);
      if (!this.closed) {
        throw new Error('The log stream disconnected.');
      }
    } catch {
      if (!this.closed) {
        const error = new Event('error');
        this.onerror?.(error);
        this.dispatchEvent(error);
        if (this.closed) {
          return;
        }
        this.reconnectTimeout = setTimeout(() => {
          this.reconnectTimeout = null;
          void this.connect();
        }, this.reconnectDelay);
        this.reconnectDelay = Math.min(this.reconnectDelay * 2, 10000);
      }
    } finally {
      this.controller?.abort();
      this.controller = null;
    }
  }

  private async readEvents(body: ReadableStream<Uint8Array>): Promise<void> {
    const reader = body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    let eventName = '';
    let data: string[] = [];

    const acceptLine = (line: string): void => {
      if (line === '') {
        if (data.length > 0 && !this.closed) {
          const eventData = data.join('\n');
          const type = eventName || 'message';
          if (type === 'logBatch') {
            const batch = JSON.parse(eventData) as { nextCursor?: string | null };
            this.cursor = batch.nextCursor ?? this.cursor;
          }
          this.dispatchEvent(new MessageEvent(type, { data: eventData }));
        }
        eventName = '';
        data = [];
        return;
      }

      const separator = line.indexOf(':');
      const field = separator < 0 ? line : line.slice(0, separator);
      const value = separator < 0 ? '' : line.slice(separator + 1).replace(/^ /, '');
      if (field === 'event') {
        eventName = value;
      } else if (field === 'data') {
        data.push(value);
      }
    };

    try {
      while (!this.closed) {
        const { value, done } = await reader.read();
        buffer += decoder.decode(value, { stream: !done });
        let lineEnd: number;
        while ((lineEnd = buffer.search(/[\r\n]/)) >= 0) {
          // A CRLF delimiter can itself be split across network chunks.
          if (!done && buffer[lineEnd] === '\r' && lineEnd === buffer.length - 1) {
            break;
          }
          const delimiterLength = buffer.slice(lineEnd, lineEnd + 2) === '\r\n' ? 2 : 1;
          const line = buffer.slice(0, lineEnd);
          buffer = buffer.slice(lineEnd + delimiterLength);
          acceptLine(line);
        }
        if (done) {
          return;
        }
      }
    } finally {
      reader.releaseLock();
    }
  }
}
