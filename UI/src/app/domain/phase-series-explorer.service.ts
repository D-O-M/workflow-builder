import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { ParamMap } from '@angular/router';
import { Observable } from 'rxjs';
import type {
  PhasedBlueprintPhaseVersionPolicy,
  PhasedProcessDetailViewModel,
  PhasedProcessPhaseViewModel,
  PhasedProcessStatus,
} from './blueprint-editor.service';

type PhaseSeriesHeader = Omit<PhasedProcessDetailViewModel, 'phases'>;

export interface PhaseSeriesSummary extends PhaseSeriesHeader {
  currentPhaseName: string | null;
  currentPhaseIndex: number | null;
  phaseCount: number;
  completedPhaseCount: number;
}

export interface PhaseSeriesSearchPage {
  items: PhaseSeriesSummary[];
  continuationToken: string | null;
}

export interface PhaseSeriesPhase extends Omit<PhasedProcessPhaseViewModel, 'phaseResult' | 'childProcess'> {
  versionPolicy: PhasedBlueprintPhaseVersionPolicy;
  blueprintVersionNumber: number | null;
}

export interface PhaseSeriesTransition {
  transitionId: string;
  sourcePhaseId: string;
  destinationPhaseId: string;
}

export interface PhaseSeriesOverview extends PhaseSeriesHeader {
  currentPhaseId: string | null;
  phases: PhaseSeriesPhase[];
  transitions: PhaseSeriesTransition[];
}

export interface HandoffValues extends PhaseSeriesTransition {
  mappings: {
    sourceNodeId: string | null;
    sourceNodeName: string | null;
    sourceChannel: string | null;
    sourceProperty: string;
    targetNodeId: string;
    targetNodeName: string | null;
    targetProperty: string;
    hasValue: boolean;
    valueJson: string | null;
  }[];
  unattributedValues: { property: string; valueJson: string }[];
}

export const phaseSeriesStatusLabels: Record<PhasedProcessStatus, string> = {
  InProgress: 'Running',
  WaitingForTrigger: 'Waiting for trigger',
  Completed: 'Completed',
  Failed: 'Failed',
  Pending: 'Pending',
};

export interface PhaseSeriesFilters {
  q: string;
  status: string;
}

export function readPhaseSeriesFilters(params: ParamMap): PhaseSeriesFilters {
  return {
    q: (params.get('q') ?? '').trim(),
    status: params.get('status') ?? '',
  };
}

export function phaseSeriesFilterError(filters: PhaseSeriesFilters): string | null {
  if (filters.q.length > 200) return 'Enter a series name or parent ID of up to 200 characters.';
  if (filters.status && !Object.hasOwn(phaseSeriesStatusLabels, filters.status)) {
    return 'Choose a listed status.';
  }
  return null;
}

@Injectable({ providedIn: 'root' })
export class PhaseSeriesExplorerService {
  private readonly http = inject(HttpClient);

  search(filters: PhaseSeriesFilters, continuationToken?: string | null): Observable<PhaseSeriesSearchPage> {
    let params = new HttpParams().set('pageSize', 25);
    if (filters.q) params = params.set('q', filters.q);
    if (filters.status) params = params.set('status', filters.status);
    if (continuationToken) params = params.set('continuationToken', continuationToken);
    return this.http.get<PhaseSeriesSearchPage>('api/phased-processes', { params });
  }

  overview(definitionId: number, parentId: string): Observable<PhaseSeriesOverview> {
    return this.http.get<PhaseSeriesOverview>(`api/phased-blueprints/${definitionId}/processes/${encodeURIComponent(parentId)}/overview`);
  }

  handoff(definitionId: number, parentId: string, sourcePhaseId: string): Observable<HandoffValues> {
    return this.http.get<HandoffValues>(
      `api/phased-blueprints/${definitionId}/processes/${encodeURIComponent(parentId)}/handoffs/${encodeURIComponent(sourcePhaseId)}`,
    );
  }
}
