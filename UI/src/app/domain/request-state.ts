import { Observable, catchError, map, of, startWith, switchMap } from 'rxjs';

export type RequestState<T> =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'ready'; data: T }
  | { status: 'error'; message: string };

export function idleRequestState<T>(): RequestState<T> {
  return { status: 'idle' };
}

export function loadingRequestState<T>(): RequestState<T> {
  return { status: 'loading' };
}

export function readyRequestState<T>(data: T): RequestState<T> {
  return { status: 'ready', data };
}

export function errorRequestState<T>(message: string): RequestState<T> {
  return { status: 'error', message };
}

export function isReadyRequestState<T>(
  state: RequestState<T>,
): state is Extract<RequestState<T>, { status: 'ready' }> {
  return state.status === 'ready';
}

export function isErrorRequestState<T>(
  state: RequestState<T>,
): state is Extract<RequestState<T>, { status: 'error' }> {
  return state.status === 'error';
}

export function createRequestStateStream<TParams, TResult>(
  params$: Observable<TParams | null | undefined>,
  loader: (params: TParams) => Observable<TResult>,
  errorMessage: string,
): Observable<RequestState<TResult>> {
  return params$.pipe(
    switchMap((params) => {
      if (params === null || params === undefined) {
        return of(idleRequestState<TResult>());
      }

      return loader(params).pipe(
        map((data) => readyRequestState(data)),
        startWith(loadingRequestState<TResult>()),
        catchError(() => of(errorRequestState<TResult>(errorMessage))),
      );
    }),
  );
}
