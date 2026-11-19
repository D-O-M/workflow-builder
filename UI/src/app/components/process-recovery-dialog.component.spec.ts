import '@angular/compiler';
import { createEnvironmentInjector, runInInjectionContext } from '@angular/core';
import { MAT_DIALOG_DATA } from '@angular/material/dialog';
import { describe, expect, it, vi } from 'vitest';
import { ProcessRecoveryDialogComponent } from './process-recovery-dialog.component';
import { ProcessRecoveryService, RecoveryCase, RecoveryPage, RecoveryStep } from '../domain/process-recovery.service';

describe('Process recovery preview', () => {
  it('uses the revision policy even when a page contains only a changed case, and explicitly restores Pause', async () => {
    const changed: RecoveryCase = { processId: 'a', blueprintDefinitionId: 1, blueprintVersionId: 1, status: 'Changed',
      plan: { steps: [], continuationStepIds: [], targetStepIds: [], newWorkPolicy: 'Pause', issues: [] } };
    const initial: RecoveryPage = { id: 'batch', revision: 2, status: 'Preview', processCount: 26, stepCount: 1,
      newWorkPolicy: 'Run', eligibleProcessCount: 1, createdAt: '', actor: 'operator', confirmedRevisions: [],
      cases: [changed], hasMore: true };
    const next: RecoveryPage = { ...initial, hasMore: false, cases: [{ ...changed, processId: 'b', status: 'Preview',
      plan: { ...changed.plan!, newWorkPolicy: 'Run' } }] };
    const revised: RecoveryPage = { ...initial, revision: 3, newWorkPolicy: 'Pause', eligibleProcessCount: 0 };
    const api = { preview: vi.fn().mockResolvedValue(initial), get: vi.fn().mockResolvedValue(next),
      revise: vi.fn().mockResolvedValue(revised), confirm: vi.fn() };
    const injector = createEnvironmentInjector([
      { provide: ProcessRecoveryService, useValue: api }, { provide: MAT_DIALOG_DATA, useValue: { selection: { allMatches: true } } },
    ], null as never);
    try {
      const preview = runInInjectionContext(injector, () => new ProcessRecoveryDialogComponent());
      await vi.waitFor(() => expect(preview['busy']()).toBe(false));
      expect(preview['newWorkPolicy']()).toBe('Run');
      await preview['open']('batch', 25);
      expect(preview['newWorkPolicy']()).toBe('Run');
      preview['newWorkPolicy'].set('Pause');
      await preview['revise']();
      expect(api.revise).toHaveBeenCalledWith(next, 'Pause', []);
      expect(preview['hasEligibleCases']()).toBe(false);
      preview['reason'].set('Already repaired');
      await preview['confirm']();
      expect(api.confirm).not.toHaveBeenCalled();
    } finally {
      injector.destroy();
    }
  });

  it('counts actionable occurrences per version across pages and applies an item exception to a group decision', async () => {
    const payment = (id: string, inScope: boolean): RecoveryStep => ({
      stepId: id, logicalStepId: id, blueprintNodeId: 'payment', name: 'Take payment',
      status: 'Completed', action: 'Reuse', inScope, canStartHere: true, history: [], missingInputs: [],
    });
    const plan = (processId: string, blueprintVersionId: number, steps: RecoveryStep[]): RecoveryCase => ({
      processId, blueprintDefinitionId: 1, blueprintVersionId, status: 'Preview',
      plan: { steps, continuationStepIds: [], targetStepIds: [], newWorkPolicy: 'Pause', issues: [] },
    });
    const first = plan('a', 1, [payment('a-1', true), payment('a-2', true), payment('a-3', false)]);
    const page: RecoveryPage = { id: 'batch', revision: 1, status: 'Preview', processCount: 2, stepCount: 2,
      createdAt: '', actor: 'operator', confirmedRevisions: [], cases: [first], hasMore: true };
    const api = { preview: vi.fn().mockResolvedValue(page),
      get: vi.fn().mockResolvedValue({ ...page, cases: [plan('b', 2, [payment('b-1', false)])], hasMore: false }) };
    const injector = createEnvironmentInjector([
      { provide: ProcessRecoveryService, useValue: api }, { provide: MAT_DIALOG_DATA, useValue: { selection: { allMatches: true } } },
    ], null as never);
    try {
      const preview = runInInjectionContext(injector, () => new ProcessRecoveryDialogComponent());
      await vi.waitFor(() => expect(preview['busy']()).toBe(false));
      await preview['loadGroups']();
      const [v1, v2] = preview['groups']()!;
      expect(v1).toMatchObject({ blueprintVersionId: 1, availableSteps: 3, inScopeSteps: 2, affectedProcesses: 1, eligibleStarts: 3 });
      expect(v2).toMatchObject({ blueprintVersionId: 2, availableSteps: 1, inScopeSteps: 0, affectedProcesses: 0, eligibleStarts: 1 });
      preview['groupEdit'](v1!, { action: 'Run' });
      const [one, two] = first.plan!.steps;
      preview['changeAction'](first, two!, 'Reuse');
      expect(preview['draft'](first, one!).action).toBe('Run');
      expect(preview['draft'](first, two!).action).toBe('Reuse');
    } finally {
      injector.destroy();
    }
  });
});
