import { DatePipe, JsonPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import {
  NewWorkPolicy, ProcessRecoveryService, RecoveryAction, RecoveryCase, RecoveryEdit,
  RecoveryPage, RecoveryRecordedOutcome, RecoverySelection, RecoveryStep, RecoverySummary,
} from '../domain/process-recovery.service';

export type RecoveryDialogData =
  | { selection: RecoverySelection }
  | { recoveryId: string }
  | { history: true; processId?: string };

interface RecoveryGroup {
  blueprintVersionId: number;
  blueprintNodeId: string;
  name: string;
  affectedProcesses: number;
  inScopeSteps: number;
  availableSteps: number;
  eligibleStarts: number;
  hasUnrecordedSteps: boolean;
}

@Component({
  selector: 'app-process-recovery-dialog',
  imports: [DatePipe, JsonPipe, MatButtonModule, MatDialogModule, MatIconModule, MatProgressBarModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './process-recovery-dialog.component.html',
  styleUrl: './process-recovery-dialog.component.css',
})
export class ProcessRecoveryDialogComponent {
  private readonly api = inject(ProcessRecoveryService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly data = inject<RecoveryDialogData>(MAT_DIALOG_DATA);
  protected readonly page = signal<RecoveryPage | null>(null);
  protected readonly history = signal<RecoverySummary[] | null>(null);
  protected readonly groups = signal<RecoveryGroup[] | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly offset = signal(0);
  protected readonly historicalRevision = signal<number | undefined>(undefined);
  protected readonly reason = signal('');
  protected readonly newWorkPolicy = signal<NewWorkPolicy>('Pause');
  protected readonly edits = signal<RecoveryEdit[]>([]);
  protected readonly editable = computed(() => {
    return this.historicalRevision() === undefined && this.page()?.status === 'Preview';
  });
  protected readonly hasEligibleCases = computed(() => {
    const page = this.page();
    return !!page && page.stepCount > 0 && (page.eligibleProcessCount ?? page.processCount) > 0;
  });
  protected readonly canResume = computed(() => this.historicalRevision() === undefined && this.page()?.status === 'Completed'
    && this.page()?.cases.some(item => item.status === 'Held' || item.status === 'Failed'));
  protected readonly dirty = computed(() => this.edits().length > 0 || this.newWorkPolicy() !== this.savedPolicy());
  protected readonly actions: ReadonlyArray<{ value: RecoveryAction; label: string }> = [
    { value: 'Run', label: 'Run' }, { value: 'Reuse', label: 'Use recorded outcome' }, { value: 'Skip', label: 'Skip' },
  ];
  private readonly savedPolicy = signal<NewWorkPolicy>('Pause');
  private pollTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    this.destroyRef.onDestroy(() => clearTimeout(this.pollTimer));
    void this.initialize();
  }

  protected async initialize(historyOffset = 0) {
    const data = this.data;
    if ('history' in data) {
      await this.request(async () => {
        const rows = await this.api.history(data.processId, historyOffset);
        this.page.set(null);
        this.offset.set(historyOffset);
        this.historicalRevision.set(undefined);
        this.history.set(rows);
      });
    } else if ('selection' in data) {
      await this.request(async () => this.acceptPage(await this.api.preview(data.selection)));
    } else {
      await this.open(data.recoveryId);
    }
  }

  protected async open(id: string, offset = 0, revision?: number) {
    await this.request(async () => {
      const page = await this.api.get(id, offset, 25, revision);
      if (id !== this.page()?.id) this.reason.set('');
      this.historicalRevision.set(revision);
      this.acceptPage(page, offset);
    });
  }

  protected setEdit(edit: RecoveryEdit) {
    this.error.set('');
    this.edits.update(items => {
      const existing = items.find(item => this.sameScope(item, edit));
      return [...items.filter(item => item !== existing), { ...existing, ...edit }];
    });
  }

  private sameScope(a: RecoveryEdit, b: RecoveryEdit): boolean {
    return a.blueprintVersionId === b.blueprintVersionId && a.blueprintNodeId === b.blueprintNodeId
      && a.processId === b.processId && a.stepId === b.stepId;
  }

  protected scope(item: RecoveryCase, step: RecoveryStep): RecoveryEdit {
    return { blueprintVersionId: item.blueprintVersionId, blueprintNodeId: step.blueprintNodeId,
      processId: item.processId, stepId: step.stepId };
  }

  protected draft(item: RecoveryCase, step: RecoveryStep) {
    const matching = this.edits().filter(edit => edit.blueprintVersionId === item.blueprintVersionId
      && edit.blueprintNodeId === step.blueprintNodeId && (!edit.processId || edit.processId === item.processId)
      && (!edit.stepId || edit.stepId === step.stepId));
    const occurrence = matching.find(edit => edit.stepId === step.stepId);
    const group = matching.find(edit => !edit.processId);
    return { action: occurrence?.action ?? group?.action ?? step.action,
      source: occurrence?.reuseStepId ?? step.reuseStepId ?? step.stepId };
  }

  protected groupEdit(group: RecoveryGroup, change: Partial<Pick<RecoveryEdit, 'action' | 'continuation'>>) {
    this.setEdit({ blueprintVersionId: group.blueprintVersionId, blueprintNodeId: group.blueprintNodeId, ...change });
  }

  protected groupAction(group: RecoveryGroup) {
    return this.edits().find(edit => !edit.processId && edit.blueprintVersionId === group.blueprintVersionId
      && edit.blueprintNodeId === group.blueprintNodeId)?.action ?? '';
  }

  protected changeAction(item: RecoveryCase, step: RecoveryStep, action: RecoveryAction) {
    this.setEdit({ ...this.scope(item, step), action, reuseStepId: action === 'Reuse' ? this.draft(item, step).source : undefined });
  }

  protected recorded(item: RecoveryCase, step: RecoveryStep) {
    return step.history?.find(attempt => attempt.stepId === this.draft(item, step).source) ?? step;
  }

  protected recordedStatus(record: RecoveryRecordedOutcome): string {
    switch (record.recoveryAction) {
      case 'Reuse': return `${record.status} · Used recorded outcome`;
      case 'Skip': return `${record.status} · Skipped by operator`;
      case 'Run': return `${record.status} · Ran during recovery`;
      default: return record.status;
    }
  }

  protected async loadGroups() {
    const current = this.page();
    if (!current) return;
    await this.request(async () => {
      const groups = new Map<string, RecoveryGroup>();
      let offset = 0;
      let result: RecoveryPage;
      do {
        result = offset === this.offset() ? current : await this.api.get(current.id, offset);
        if (this.destroyRef.destroyed) return;
        if (result.revision !== current.revision) throw new Error('This preview changed. Reopen it to review the latest revision.');
        for (const item of result.cases) {
          if (item.status !== 'Preview' && item.status !== 'Held') continue;
          const seen = new Set<string>();
          for (const step of item.plan?.steps ?? []) {
            const key = `${item.blueprintVersionId}/${step.blueprintNodeId}`;
            const group = groups.get(key) ?? { blueprintVersionId: item.blueprintVersionId, blueprintNodeId: step.blueprintNodeId,
              name: step.name, affectedProcesses: 0, inScopeSteps: 0, availableSteps: 0, eligibleStarts: 0, hasUnrecordedSteps: false };
            group.availableSteps++;
            if (step.canStartHere) group.eligibleStarts++;
            if (step.inScope) {
              group.inScopeSteps++;
              if (!seen.has(key)) group.affectedProcesses++;
              seen.add(key);
              group.hasUnrecordedSteps ||= step.status === 'NotExecuted';
            }
            groups.set(key, group);
          }
        }
        offset += result.cases.length;
      } while (result.hasMore && result.cases.length);
      this.groups.set([...groups.values()]);
    });
  }

  protected async revise() {
    const page = this.page();
    if (!page || (!this.editable() && !this.canResume())) return;
    const policy = this.newWorkPolicy() === this.savedPolicy() ? undefined : this.newWorkPolicy();
    await this.request(async () => this.acceptPage(await this.api.revise(page, policy, this.edits())));
  }

  protected async confirm() {
    const page = this.page();
    if (!page || page.status !== 'Preview' || this.dirty() || !this.reason().trim() || !this.hasEligibleCases()) return;
    await this.request(async () => this.acceptPage(await this.api.confirm(page, this.reason().trim())));
  }

  private acceptPage(page: RecoveryPage, offset = 0) {
    this.page.set(page);
    this.offset.set(offset);
    this.edits.set([]);
    this.groups.set(null);
    const policy = page.newWorkPolicy ?? 'Pause';
    this.savedPolicy.set(policy);
    this.newWorkPolicy.set(policy);
  }

  private async request(task: () => Promise<void>) {
    if (this.busy() || this.destroyRef.destroyed) return;
    clearTimeout(this.pollTimer);
    this.busy.set(true);
    this.error.set('');
    try {
      await task();
    } catch (error) {
      this.error.set(this.errorMessage(error));
    } finally {
      this.busy.set(false);
      const page = this.page();
      if (!this.destroyRef.destroyed && !this.error() && page?.status === 'Confirmed' && this.historicalRevision() === undefined) {
        this.pollTimer = setTimeout(() => void this.open(page.id, this.offset()), 3000);
      }
    }
  }

  private errorMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 403) return 'Process Operator access is required to recover Steps.';
      const message = error.error?.message ?? error.error?.error ?? error.error?.detail;
      if (typeof message === 'string') return message;
      if (error.status === 409) return 'This recovery changed. Reload the preview before making another decision.';
    }
    return error instanceof Error && !(error instanceof HttpErrorResponse)
      ? error.message : 'Unable to load or update the recovery. Try again.';
  }

}
