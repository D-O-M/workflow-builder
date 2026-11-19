import '@angular/compiler';
import { createEnvironmentInjector, EnvironmentInjector, runInInjectionContext } from '@angular/core';
import { ActivatedRoute, convertToParamMap, Router } from '@angular/router';
import { BehaviorSubject, of, Subject, throwError } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  PhaseSeriesExplorerService, PhaseSeriesSearchPage, PhaseSeriesSummary,
} from '../domain/phase-series-explorer.service';
import { PhaseSeriesListPageComponent } from './phase-series-list-page.component';

const row = (id: string): PhaseSeriesSummary => ({
  phasedProcessId: id, phasedBlueprintDefinitionId: 3, phasedBlueprintName: 'Score checks',
  phasedBlueprintVersionId: 9, phasedBlueprintVersionNumber: 2, status: 'WaitingForTrigger',
  createdAt: '2026-11-06T12:00:00Z', currentPhaseIndex: 1, currentPhaseName: 'Score again',
  phaseCount: 2, completedPhaseCount: 1,
});

describe('Phase Series search', () => {
  let injector: EnvironmentInjector;
  const params = new BehaviorSubject(convertToParamMap({}));
  const search = vi.fn();
  function create(query: Record<string, string> = {}) {
    params.next(convertToParamMap(query));
    injector = createEnvironmentInjector([
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: params.value }, queryParamMap: params } },
      { provide: Router, useValue: { navigate: vi.fn() } },
      { provide: PhaseSeriesExplorerService, useValue: { search } },
    ], null as never);
    return runInInjectionContext(injector, () => new PhaseSeriesListPageComponent());
  }

  afterEach(() => {
    injector.destroy();
    search.mockReset();
  });

  it('restores URL filters and cancels a previous search when they change', () => {
    const stale = new Subject<PhaseSeriesSearchPage>();
    search.mockReturnValueOnce(stale).mockReturnValueOnce(of({ items: [row('new')], continuationToken: null }));
    const page = create({ q: 'score', status: 'Failed' });
    expect(page['form'].controls.q.value).toBe('score');
    params.next(convertToParamMap({ status: 'WaitingForTrigger' }));
    expect(stale.observed).toBe(false);
    expect(page['rows']().map(r => r.phasedProcessId)).toEqual(['new']);
    expect(page['form'].controls.q.value).toBe('');
  });

  it('keeps the page and cursor on a failed load-more request, then merges once per parent', () => {
    search.mockReturnValueOnce(of({ items: [row('one')], continuationToken: 'next' }))
      .mockReturnValueOnce(throwError(() => new Error('unavailable')))
      .mockReturnValueOnce(of({ items: [row('one'), row('two')], continuationToken: null }));
    const page = create();
    page['load']();
    expect(page['rows']()).toHaveLength(1);
    expect(page['cursor']()).toBe('next');
    expect(page['error']()).toContain('Unable');
    page['load']();
    expect(page['rows']().map(r => r.phasedProcessId)).toEqual(['one', 'two']);
    expect(search.mock.calls[2][1]).toBe('next');
  });

  it('rejects an unknown status on a shared URL before searching', () => {
    const page = create({ status: 'Unknown' });
    expect(page['formError']()).toContain('status');
    expect(search).not.toHaveBeenCalled();
  });

  it('restarts from page one when Search is submitted with the current filters', () => {
    search.mockReturnValueOnce(of({ items: [row('old')], continuationToken: 'next' }))
      .mockReturnValueOnce(of({ items: [row('new')], continuationToken: null }));
    const page = create({ q: 'score' });
    page['submit']();
    expect(search.mock.calls[1]).toEqual([{ q: 'score', status: '' }, null]);
    expect(page['rows']().map(r => r.phasedProcessId)).toEqual(['new']);
  });
});
