import '@angular/compiler';
import { DOCUMENT } from '@angular/common';
import { Clipboard } from '@angular/cdk/clipboard';
import { createEnvironmentInjector, EnvironmentInjector, runInInjectionContext } from '@angular/core';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject, of, Subject, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PhaseSeriesExplorerService, PhaseSeriesOverview } from '../domain/phase-series-explorer.service';
import { PhaseSeriesOverviewPageComponent } from './phase-series-overview-page.component';

const firstId = 'a3000000-0000-0000-0000-000000000001';
const secondId = 'a3000000-0000-0000-0000-000000000002';
const parent = (status: PhaseSeriesOverview['status']): PhaseSeriesOverview => ({
  phasedProcessId: firstId, phasedBlueprintDefinitionId: 5, phasedBlueprintName: 'Score checks',
  phasedBlueprintVersionId: 24, phasedBlueprintVersionNumber: 2, status,
  createdAt: '2026-11-06T12:00:00Z', currentPhaseId: null, phases: [], transitions: [],
});

describe('Phase Series overview refresh', () => {
  let injector: EnvironmentInjector;
  const params = new BehaviorSubject(convertToParamMap({ definitionId: 5, parentId: firstId }));
  let document: EventTarget & { hidden: boolean; defaultView: EventTarget };
  let overview: ReturnType<typeof vi.fn>;

  function create() {
    injector = createEnvironmentInjector([
      { provide: ActivatedRoute, useValue: { paramMap: params } },
      { provide: DOCUMENT, useValue: document },
      { provide: Clipboard, useValue: { copy: () => true } },
      { provide: PhaseSeriesExplorerService, useValue: { overview } },
    ], null as never);
    return runInInjectionContext(injector, () => new PhaseSeriesOverviewPageComponent());
  }

  beforeEach(() => {
    vi.useFakeTimers();
    params.next(convertToParamMap({ definitionId: 5, parentId: firstId }));
    overview = vi.fn();
    document = Object.assign(new EventTarget(), { hidden: false, defaultView: new EventTarget() });
  });

  afterEach(() => {
    injector.destroy();
    vi.useRealTimers();
  });

  it.each([503, 404])('retains a loaded parent on HTTP %s and stops on completion', status => {
    const waiting = parent('WaitingForTrigger');
    overview.mockReturnValueOnce(of(waiting))
      .mockReturnValueOnce(throwError(() => ({ status })))
      .mockReturnValueOnce(of(parent('Completed')));
    const page = create();
    vi.advanceTimersByTime(5000);
    expect(page['overview']()).toBe(waiting);
    expect(page['error']()).toContain('last update');
    page['refresh']();
    expect(page['overview']()?.status).toBe('Completed');
    expect(page['error']()).toBe('');
    vi.advanceTimersByTime(30000);
    document.defaultView.dispatchEvent(new Event('focus'));
    expect(overview).toHaveBeenCalledTimes(3);
  });

  it('pauses while hidden, resumes on focus and discards prior route responses', () => {
    const first = new Subject<PhaseSeriesOverview>();
    const next = new Subject<PhaseSeriesOverview>();
    overview.mockReturnValueOnce(first).mockReturnValueOnce(next).mockReturnValue(of(parent('InProgress')));
    create();
    params.next(convertToParamMap({ definitionId: 5, parentId: secondId }));
    expect(first.observed).toBe(false);
    expect(overview).toHaveBeenLastCalledWith(5, secondId);
    document.hidden = true;
    document.dispatchEvent(new Event('visibilitychange'));
    expect(next.observed).toBe(false);
    vi.advanceTimersByTime(20000);
    expect(overview).toHaveBeenCalledTimes(2);
    document.hidden = false;
    document.defaultView.dispatchEvent(new Event('focus'));
    expect(overview).toHaveBeenCalledTimes(3);
  });

  it('stops polling a missing parent and permits an explicit retry', () => {
    overview.mockReturnValueOnce(throwError(() => ({ status: 404 }))).mockReturnValueOnce(of(parent('Completed')));
    const page = create();
    expect(page['error']()).toContain('could not be found');
    vi.advanceTimersByTime(20000);
    expect(overview).toHaveBeenCalledTimes(1);
    page['refresh']();
    expect(page['overview']()?.status).toBe('Completed');
  });

  it('declines malformed routes without offering an ineffective retry', () => {
    params.next(convertToParamMap({ definitionId: 5, parentId: 'invalid' }));
    const page = create();
    expect(page['invalidLink']()).toBe(true);
    page['refresh']();
    expect(overview).not.toHaveBeenCalled();
  });
});
