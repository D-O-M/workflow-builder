import { Subject } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { createRequestStateStream } from './request-state';

describe('createRequestStateStream', () => {
  it('only emits the latest ready state when a newer request supersedes an older one', () => {
    const params$ = new Subject<number | null>();
    const pendingResponses = new Map<number, Subject<string>>();
    const states: Array<{ status: string; data?: string }> = [];

    const subscription = createRequestStateStream(
      params$,
      (requestId) => {
        const response$ = new Subject<string>();
        pendingResponses.set(requestId, response$);
        return response$;
      },
      'Unable to load data.',
    ).subscribe((state) => {
      if (state.status === 'ready') {
        states.push({ status: state.status, data: state.data });
        return;
      }

      states.push({ status: state.status });
    });

    params$.next(1);
    params$.next(2);
    pendingResponses.get(1)?.next('stale');
    pendingResponses.get(2)?.next('fresh');

    expect(states).toEqual([
      { status: 'loading' },
      { status: 'loading' },
      { status: 'ready', data: 'fresh' },
    ]);

    subscription.unsubscribe();
  });
});
