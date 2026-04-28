import { DatePipe, NgClass } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { catchError, distinctUntilChanged, forkJoin, map, of, startWith } from 'rxjs';
import {
  BlueprintEditorService,
  BlueprintViewModel,
} from '../domain/blueprint-editor.service';
import {
  NodeObservabilitySummaryViewModel,
  ObservabilityService,
  ObservabilityWindow,
  RecentProcessViewModel,
  ProcessObservabilitySummaryViewModel,
} from '../domain/observability.service';
import {
  createRequestStateStream,
  errorRequestState,
  isErrorRequestState,
  isReadyRequestState,
  loadingRequestState,
  readyRequestState,
} from '../domain/request-state';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';

interface DashboardObservabilityData {
  processSummary: ProcessObservabilitySummaryViewModel;
  recentProcesses: RecentProcessViewModel[];
  nodeSummary: NodeObservabilitySummaryViewModel | null;
}

type DashboardStepSummary = NodeObservabilitySummaryViewModel['nodes'][number];

interface StepReliabilityCounts {
  failing: number;
  degraded: number;
  healthy: number;
  idle: number;
}

@Component({
  selector: 'app-dashboard-page',
  imports: [
    DatePipe,
    NgClass,
    RouterLink,
    MatButtonToggleModule,
    MatButtonModule,
    MatCardModule,
    MatChipsModule,
    MatDividerModule,
    MatFormFieldModule,
    MatIconModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatSelectModule,
  ],
  templateUrl: './dashboard-page.component.html',
  styleUrl: './dashboard-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardPageComponent {
  private readonly blueprintService = inject(BlueprintEditorService);
  private readonly observabilityService = inject(ObservabilityService);
  private readonly runtimeClientConfig = inject(RuntimeClientConfigService);

  protected readonly selectedBlueprintId = signal<number | null>(null);
  protected readonly observabilityWindow = signal<ObservabilityWindow>('1h');
  protected readonly windowPresets: ObservabilityWindow[] = ['15m', '1h', '6h', '24h', '7d'];

  private readonly blueprintsState = toSignal(
    this.blueprintService.getBlueprints().pipe(
      map((blueprints) => readyRequestState<BlueprintViewModel[]>(blueprints)),
      startWith(loadingRequestState<BlueprintViewModel[]>()),
      catchError(() => of(errorRequestState<BlueprintViewModel[]>('Unable to load blueprints for the dashboard.'))),
    ),
    { initialValue: loadingRequestState<BlueprintViewModel[]>() },
  );

  private readonly observabilityState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => ({
        window: this.observabilityWindow(),
        blueprintDefinitionId: this.selectedBlueprintId(),
      }))).pipe(
        distinctUntilChanged((left, right) =>
          left.window === right.window &&
          left.blueprintDefinitionId === right.blueprintDefinitionId,
        ),
      ),
      ({ window, blueprintDefinitionId }) => forkJoin({
        processSummary: this.observabilityService.getProcessSummary(window, blueprintDefinitionId),
        recentProcesses: this.observabilityService.getRecentProcesses(window, blueprintDefinitionId, 8),
        nodeSummary: blueprintDefinitionId
          ? this.observabilityService.getNodeSummary(window, blueprintDefinitionId)
          : of(null),
      }).pipe(
        map((data): DashboardObservabilityData => ({
          processSummary: data.processSummary,
          recentProcesses: data.recentProcesses.processes,
          nodeSummary: data.nodeSummary,
        })),
      ),
      'Unable to load dashboard observability right now.',
    ),
    { initialValue: loadingRequestState<DashboardObservabilityData>() },
  );

  protected readonly loading = computed(() => this.blueprintsState().status === 'loading');
  protected readonly observabilityLoading = computed(() => this.observabilityState().status === 'loading');
  protected readonly blueprints = computed<BlueprintViewModel[]>(() => {
    const state = this.blueprintsState();
    return isReadyRequestState(state) ? state.data : [];
  });
  protected readonly selectedBlueprint = computed<BlueprintViewModel | null>(() =>
    this.blueprints().find((blueprint) => blueprint.id === this.selectedBlueprintId()) ?? null,
  );
  protected readonly selectedBlueprintLabel = computed(() =>
    this.selectedBlueprint()?.name ?? 'All blueprints',
  );
  protected readonly processSummary = computed<ProcessObservabilitySummaryViewModel | null>(() => {
    const state = this.observabilityState();
    return isReadyRequestState(state) ? state.data.processSummary : null;
  });
  protected readonly nodeSummary = computed<NodeObservabilitySummaryViewModel | null>(() => {
    const state = this.observabilityState();
    return isReadyRequestState(state) ? state.data.nodeSummary : null;
  });
  protected readonly rankedStepNodes = computed<DashboardStepSummary[]>(() =>
    [...(this.nodeSummary()?.nodes ?? [])].sort((left, right) => {
      const rankDifference = this.nodeHealthRank(left) - this.nodeHealthRank(right);
      if (rankDifference !== 0) {
        return rankDifference;
      }

      const failureDifference = right.failedCount - left.failedCount;
      if (failureDifference !== 0) {
        return failureDifference;
      }

      return left.taskName.localeCompare(right.taskName);
    }),
  );
  protected readonly stepReliabilityCounts = computed<StepReliabilityCounts>(() =>
    this.rankedStepNodes().reduce<StepReliabilityCounts>((counts, node) => {
      switch (this.nodeHealthRank(node)) {
        case 0:
          counts.failing += 1;
          break;
        case 1:
          counts.degraded += 1;
          break;
        case 2:
          counts.healthy += 1;
          break;
        default:
          counts.idle += 1;
          break;
      }

      return counts;
    }, {
      failing: 0,
      degraded: 0,
      healthy: 0,
      idle: 0,
    }),
  );
  protected readonly stepReliabilitySummary = computed(() => {
    const counts = this.stepReliabilityCounts();
    const parts = [
      this.formatStepCount(counts.failing, 'failing'),
      this.formatStepCount(counts.degraded, 'degraded'),
      this.formatStepCount(counts.healthy, 'healthy'),
      this.formatStepCount(counts.idle, 'idle'),
    ].filter((part): part is string => part !== null);

    return parts.length > 0 ? parts.join(' · ') : 'No step outcomes';
  });
  protected readonly recentProcesses = computed<RecentProcessViewModel[]>(() => {
    const state = this.observabilityState();
    return isReadyRequestState(state) ? state.data.recentProcesses : [];
  });
  protected readonly errorMessage = computed(() => {
    const blueprintsState = this.blueprintsState();
    if (isErrorRequestState(blueprintsState)) {
      return blueprintsState.message;
    }

    const observabilityState = this.observabilityState();
    if (isErrorRequestState(observabilityState)) {
      return observabilityState.message;
    }

    return null;
  });
  protected readonly grafanaLogsUrl = computed(() => {
    const blueprintId = this.selectedBlueprintId();
    const expression = blueprintId
      ? `{blueprint_definition_id="${blueprintId}"}`
      : `{service_name=~".+"}`;

    const left = encodeURIComponent(JSON.stringify({
      datasource: 'loki',
      queries: [
        {
          refId: 'A',
          expr: expression,
        },
      ],
      range: {
        from: `now-${this.observabilityWindow()}`,
        to: 'now',
      },
    }));

    return `${this.runtimeClientConfig.grafanaBaseUrl}/explore?orgId=1&left=${left}`;
  });
  protected readonly grafanaTraceUrl = computed(() =>
    `${this.runtimeClientConfig.grafanaBaseUrl}/explore?orgId=1`,
  );
  protected readonly historyQueryParams = computed(() => {
    const blueprintId = this.selectedBlueprintId();
    return blueprintId ? { blueprintId } : {};
  });

  protected selectWindow(window: ObservabilityWindow): void {
    this.observabilityWindow.set(window);
  }

  protected selectBlueprint(blueprintId: string): void {
    const parsed = Number(blueprintId);
    this.selectedBlueprintId.set(Number.isInteger(parsed) && parsed > 0 ? parsed : null);
  }

  protected clearBlueprint(): void {
    this.selectedBlueprintId.set(null);
  }

  protected processSuccessPercent(): number {
    const summary = this.processSummary();
    if (!summary || summary.totalCount === 0) {
      return 0;
    }

    return Math.round((summary.successfulCount / summary.totalCount) * 100);
  }

  protected processFailurePercent(): number {
    const summary = this.processSummary();
    if (!summary || summary.totalCount === 0) {
      return 0;
    }

    return Math.round((summary.failedCount / summary.totalCount) * 100);
  }

  protected processHealthLabel(): string {
    const summary = this.processSummary();
    if (!summary || summary.totalCount === 0) {
      return 'No terminal processes';
    }

    if (summary.failedCount === 0) {
      return 'Healthy';
    }

    if (summary.successfulCount === 0) {
      return 'Failing';
    }

    return 'Mixed outcome';
  }

  protected processHealthClass(): string {
    const summary = this.processSummary();
    if (!summary || summary.totalCount === 0) {
      return 'dashboard-health--idle';
    }

    if (summary.failedCount === 0) {
      return 'dashboard-health--success';
    }

    if (summary.successfulCount === 0) {
      return 'dashboard-health--failed';
    }

    return 'dashboard-health--mixed';
  }

  protected nodeSuccessPercent(successRate: number, totalCount: number): number {
    return totalCount === 0 ? 0 : Math.round(successRate * 100);
  }

  protected donutBackground(successfulCount: number, failedCount: number): string {
    const total = successfulCount + failedCount;
    if (total === 0) {
      return 'conic-gradient(rgba(102, 129, 170, 0.24) 0 100%)';
    }

    const successPercent = total === 0 ? 0 : (successfulCount / total) * 100;
    return `conic-gradient(#12b76a 0 ${successPercent}%, rgba(217, 45, 32, 0.88) ${successPercent}% 100%)`;
  }

  protected recentProcessStatusLabel(status: string): string {
    switch (status) {
      case 'Completed':
        return 'Succeeded';
      case 'InProgress':
        return 'Running';
      case 'Failed':
        return 'Failed';
      case 'Pending':
        return 'Pending';
      default:
        return 'Unknown';
    }
  }

  protected recentProcessStatusClass(status: string): string {
    switch (status) {
      case 'Completed':
        return 'status-chip--success';
      case 'Failed':
        return 'status-chip--failed';
      case 'InProgress':
        return 'status-chip--running';
      case 'Pending':
        return 'status-chip--pending';
      default:
        return 'status-chip--neutral';
    }
  }

  protected failedProcessDetail(process: RecentProcessViewModel): string | null {
    if (process.status !== 'Failed') {
      return null;
    }

    const failureMessage = process.failureMessage?.trim();
    return failureMessage && failureMessage.length > 0 ? failureMessage : null;
  }

  protected shortenProcessId(processId: string): string {
    if (processId.length <= 16) {
      return processId;
    }

    return `${processId.slice(0, 8)}...${processId.slice(-4)}`;
  }

  protected nodeHealthLabel(node: DashboardStepSummary): string {
    if (node.totalCount === 0) {
      return 'Idle';
    }

    if (node.failedCount === 0) {
      return 'Healthy';
    }

    if (node.successfulCount === 0) {
      return 'Failing';
    }

    return 'Degraded';
  }

  protected nodeHealthClass(node: DashboardStepSummary): string {
    if (node.totalCount === 0) {
      return 'node-health--idle';
    }

    if (node.failedCount === 0) {
      return 'node-health--healthy';
    }

    if (node.successfulCount === 0) {
      return 'node-health--failed';
    }

    return 'node-health--degraded';
  }

  protected nodeHealthRank(node: DashboardStepSummary): number {
    if (node.totalCount === 0) {
      return 3;
    }

    if (node.successfulCount === 0 && node.failedCount > 0) {
      return 0;
    }

    if (node.failedCount > 0) {
      return 1;
    }

    return 2;
  }

  private formatStepCount(count: number, label: string): string | null {
    return count > 0 ? `${count} ${label}` : null;
  }

  protected trackBlueprint(_: number, blueprint: BlueprintViewModel): number {
    return blueprint.id;
  }

  protected trackRecentProcess(_: number, process: RecentProcessViewModel): string {
    return process.processId;
  }
}
