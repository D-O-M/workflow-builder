import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type { ProcessSearchRequest } from './blueprint-editor.service';

export type RecoveryAction = 'Run' | 'Reuse' | 'Skip';
export type NewWorkPolicy = 'Pause' | 'Run';

export interface RecoverySelection {
  processId?: string;
  runtimeStepId?: string;
  search?: ProcessSearchRequest;
  selectedProcessIds?: string[];
  excludedProcessIds?: string[];
  allMatches?: boolean;
}

export interface RecoveryEdit {
  blueprintVersionId: number;
  blueprintNodeId: string;
  processId?: string;
  stepId?: string;
  reuseStepId?: string;
  action?: RecoveryAction;
  continuation?: boolean;
}

export interface RecoveryRecordedOutcome {
  stepId: string;
  status: string;
  inputs?: unknown;
  outputs?: unknown;
  recoveryAction?: RecoveryAction | null;
  reusedFromStepId?: string | null;
}

export interface RecoveryStep extends RecoveryRecordedOutcome {
  logicalStepId: string;
  blueprintNodeId: string;
  name: string;
  action: RecoveryAction;
  inScope: boolean;
  canStartHere: boolean;
  itemKey?: string | null;
  missingInputs: string[];
  reuseStepId?: string | null;
  history: RecoveryRecordedOutcome[];
}

export interface RecoveryCase {
  processId: string;
  blueprintDefinitionId: number;
  blueprintVersionId: number;
  status: string;
  message?: string | null;
  plan?: {
    steps: RecoveryStep[];
    continuationStepIds: string[];
    targetStepIds: string[];
    newWorkPolicy: NewWorkPolicy;
    issues: string[];
  } | null;
}

export interface RecoverySummary {
  id: string;
  revision: number;
  status: string;
  processCount: number;
  stepCount: number;
  createdAt: string;
  actor: string;
  reason?: string | null;
  confirmedAt?: string | null;
}

export interface RecoveryPage extends RecoverySummary {
  newWorkPolicy?: NewWorkPolicy;
  eligibleProcessCount?: number;
  cases: RecoveryCase[];
  hasMore: boolean;
  confirmedRevisions: number[];
}

@Injectable({ providedIn: 'root' })
export class ProcessRecoveryService {
  private readonly http = inject(HttpClient);
  private readonly url = 'api/process-recoveries';

  preview(selection: RecoverySelection) {
    return firstValueFrom(this.http.post<RecoveryPage>(`${this.url}/preview`, selection));
  }

  revise(page: RecoveryPage, newWorkPolicy: NewWorkPolicy | undefined, edits: RecoveryEdit[]) {
    return firstValueFrom(this.http.put<RecoveryPage>(`${this.url}/${page.id}/preview`, {
      revision: page.revision, newWorkPolicy, edits,
    }));
  }

  confirm(page: RecoveryPage, reason: string) {
    return firstValueFrom(this.http.post<RecoveryPage>(`${this.url}/${page.id}/confirm`, {
      revision: page.revision, reason,
    }));
  }

  get(id: string, offset = 0, limit = 25, revision?: number) {
    return firstValueFrom(this.http.get<RecoveryPage>(`${this.url}/${id}`, {
      params: { offset, limit, ...(revision === undefined ? {} : { revision }) },
    }));
  }

  history(processId?: string, offset = 0) {
    return firstValueFrom(this.http.get<RecoverySummary[]>(this.url, {
      params: { offset, limit: 25, ...(processId ? { processId } : {}) },
    }));
  }
}
