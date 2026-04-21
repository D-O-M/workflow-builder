import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { DatePipe, NgClass } from '@angular/common';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatTabsModule } from '@angular/material/tabs';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  firstValueFrom,
  map,
  of,
  startWith,
  switchMap,
  tap,
} from 'rxjs';
import {
  BlueprintEditorService,
  BlueprintProcessActivityViewModel,
  BlueprintProcessHistoryEntryViewModel,
  BlueprintViewModel,
  ProcessDetailViewModel,
  ProcessStepDetailViewModel,
} from '../domain/blueprint-editor.service';
import {
  ProcessStepsUpdatedEvent,
  ProcessRealtimeService,
  ProcessSummaryUpdatedEvent,
} from '../domain/process-realtime.service';
import { NodeModel } from '../domain/node-model';
import { ConnectionModel } from '../domain/connection-model';
import { BlueprintCanvasComponent } from '../components/blueprint-canvas.component';
import {
  ProcessStepLogBatchViewModel,
  NodeLogEntryViewModel,
  ObservabilityService,
} from '../domain/observability.service';
import {
  createRequestStateStream,
  errorRequestState,
  idleRequestState,
  isErrorRequestState,
  isReadyRequestState,
  loadingRequestState,
  readyRequestState,
} from '../domain/request-state';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';

interface ProcessRouteSelection {
  blueprintId: number | null;
  processId: string | null;
}

interface StepLogHistoryRequest {
  blueprintDefinitionId: number;
  processId: string;
  blueprintNodeId: string;
  from: string;
  to: string | null;
}

type HistoryPanelMode = 'blueprints' | 'processes';
type StepInspectorTab = 'inputs' | 'outputs' | 'dataflow' | 'logs';

@Component({
  selector: 'app-process-explorer-page',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatChipsModule,
    MatDividerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatListModule,
    MatMenuModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatSidenavModule,
    MatTabsModule,
    NgClass,
    DatePipe,
    BlueprintCanvasComponent,
  ],
  templateUrl: './process-explorer-page.component.html',
  styleUrl: './process-explorer-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProcessExplorerPageComponent {
  private readonly blueprintService = inject(BlueprintEditorService);
  private readonly processRealtime = inject(ProcessRealtimeService);
  private readonly observabilityService = inject(ObservabilityService);
  private readonly runtimeClientConfig = inject(RuntimeClientConfigService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  private readonly canvasViewport = viewChild(BlueprintCanvasComponent);
  private readonly processInfoButton = viewChild<ElementRef<HTMLElement>>('processInfoButton');
  private readonly processInfoPopover = viewChild<ElementRef<HTMLElement>>('processInfoPopover');
  private readonly refreshVersion = signal(0);
  private readonly preferredProcessId = signal<string | null>(null);
  private readonly realtimeSummaryOverrides = signal(new Map<string, ProcessSummaryUpdatedEvent>());
  private readonly realtimeNodeOverrides = signal(new Map<string, ProcessStepsUpdatedEvent['nodes'][number]>());
  private readonly liveStepLogs = signal<NodeLogEntryViewModel[]>([]);
  private stepLogEventSource: EventSource | null = null;
  private readonly routeSelection = toSignal(
    this.route.queryParamMap.pipe(
      map((params): ProcessRouteSelection => {
        const blueprintId = Number(params.get('blueprintId'));
        return {
          blueprintId: Number.isInteger(blueprintId) && blueprintId > 0 ? blueprintId : null,
          processId: params.get('processId'),
        };
      }),
      distinctUntilChanged((left, right) =>
        left.blueprintId === right.blueprintId &&
        left.processId === right.processId,
      ),
    ),
    {
      initialValue: {
        blueprintId: null,
        processId: null,
      } satisfies ProcessRouteSelection,
    },
  );

  protected readonly historyCollapsed = signal(false);
  protected readonly historyPanelMode = signal<HistoryPanelMode>('blueprints');
  protected readonly selectedBlueprintId = signal<number | null>(null);
  protected readonly selectedProcessId = signal<string | null>(null);
  protected readonly selectedStepInstanceId = signal<string | null>(null);
  protected readonly stepInspectorTab = signal<StepInspectorTab>('inputs');
  protected readonly stepLogStreamConnected = signal(false);
  protected readonly processInfoOpen = signal(false);
  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });

  private readonly blueprintSearchQuery = toSignal(
    this.blueprintSearch.valueChanges.pipe(
      startWith(this.blueprintSearch.value),
      map((value) => value.trim()),
      debounceTime(200),
      distinctUntilChanged(),
    ),
    { initialValue: this.blueprintSearch.value.trim() },
  );

  private readonly blueprintsState = toSignal(
    this.blueprintService.getBlueprints().pipe(
      map((blueprints) => readyRequestState<BlueprintViewModel[]>(blueprints)),
      startWith(loadingRequestState<BlueprintViewModel[]>()),
      catchError(() => of(errorRequestState<BlueprintViewModel[]>('Unable to load blueprints.'))),
    ),
    { initialValue: loadingRequestState<BlueprintViewModel[]>() },
  );

  private readonly blueprintActivityRows = signal<BlueprintProcessActivityViewModel[]>([]);
  private readonly blueprintActivityPage = signal(1);
  private readonly blueprintActivityTotalCount = signal(0);
  protected readonly blueprintActivityHasMore = signal(false);
  protected readonly blueprintActivityLoading = signal(false);
  protected readonly blueprintActivityLoadingMore = signal(false);
  protected readonly blueprintActivityError = signal<string | null>(null);

  private readonly processHistoryBaseRows = signal<BlueprintProcessHistoryEntryViewModel[]>([]);
  private readonly processHistoryNextCursor = signal<string | null>(null);
  private readonly processHistoryHasMoreState = signal(false);
  protected readonly processHistoryLoading = signal(false);
  protected readonly processHistoryLoadingMore = signal(false);
  protected readonly processHistoryError = signal<string | null>(null);

  protected readonly loading = computed(() => this.blueprintsState().status === 'loading');
  protected readonly blueprints = computed<BlueprintViewModel[]>(() => {
    const state = this.blueprintsState();
    return isReadyRequestState(state) ? state.data : [];
  });
  protected readonly selectedBlueprint = computed<BlueprintViewModel | null>(() =>
    this.blueprints().find((candidate) => candidate.id === this.selectedBlueprintId()) ?? null,
  );
  protected readonly blueprintActivities = computed(() => this.blueprintActivityRows());
  protected readonly processHistoryRows = computed<BlueprintProcessHistoryEntryViewModel[]>(() => {
    const selectedBlueprintId = this.selectedBlueprintId();
    const rowsByProcessId = new Map<string, BlueprintProcessHistoryEntryViewModel>(
      this.processHistoryBaseRows().map((row) => [row.processId, row] as const),
    );

    if (selectedBlueprintId !== null) {
      for (const update of this.realtimeSummaryOverrides().values()) {
        if (update.blueprintDefinitionId !== selectedBlueprintId) {
          continue;
        }

        const existingRow = rowsByProcessId.get(update.processId);
        rowsByProcessId.set(update.processId, {
          processId: update.processId,
          blueprintDefinitionId: update.blueprintDefinitionId,
          blueprintName: update.blueprintName,
          blueprintVersionId: update.blueprintVersionId,
          blueprintVersionNumber: update.blueprintVersionNumber,
          status: update.status,
          createdAt: existingRow?.createdAt ?? update.createdAt,
          updatedAt: update.updatedAt ?? existingRow?.updatedAt ?? null,
        });
      }
    }

    return [...rowsByProcessId.values()]
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
  });
  protected readonly processHistoryHasMore = computed(() => this.processHistoryHasMoreState());
  protected readonly activeProcessId = computed(() => {
    const currentProcessId = this.selectedProcessId();
    if (currentProcessId) {
      return currentProcessId;
    }

    const preferredProcessIdValue = this.preferredProcessId();
    if (preferredProcessIdValue) {
      return preferredProcessIdValue;
    }

    return this.processHistoryRows()[0]?.processId ?? null;
  });
  private readonly selectedProcessState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => {
        const blueprintId = this.selectedBlueprintId();
        const processId = this.activeProcessId();
        if (!blueprintId || !processId) {
          return null;
        }

        return {
          blueprintId,
          processId,
          refreshVersion: this.refreshVersion(),
        };
      })).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintId === right?.blueprintId &&
          left?.processId === right?.processId &&
          left?.refreshVersion === right?.refreshVersion,
        ),
      ),
      ({ blueprintId, processId }) =>
        this.blueprintService.getBlueprintProcess(blueprintId, processId),
      'Unable to load the selected process.',
    ),
    { initialValue: idleRequestState<ProcessDetailViewModel>() },
  );
  protected readonly detailLoading = computed(() => this.selectedProcessState().status === 'loading');
  private readonly fetchedProcess = computed<ProcessDetailViewModel | null>(() => {
    const state = this.selectedProcessState();
    return isReadyRequestState(state) ? state.data : null;
  });
  protected readonly selectedProcess = computed<ProcessDetailViewModel | null>(() => {
    const process = this.fetchedProcess();
    if (!process) {
      return null;
    }

    const summaryOverride = this.realtimeSummaryOverrides().get(process.processId);
    const nodeOverrides = this.realtimeNodeOverrides();

    return {
      ...process,
      blueprintName: summaryOverride?.blueprintName ?? process.blueprintName,
      blueprintVersionId: summaryOverride?.blueprintVersionId ?? process.blueprintVersionId,
      blueprintVersionNumber: summaryOverride?.blueprintVersionNumber ?? process.blueprintVersionNumber,
      status: summaryOverride?.status ?? process.status,
      updatedAt: summaryOverride?.updatedAt ?? process.updatedAt,
      nodes: process.nodes.map((node) => {
        const delta = nodeOverrides.get(node.instanceId);
        if (!delta) {
          return node;
        }

        return {
          ...node,
          status: delta.status,
          resolvedVersionId: delta.resolvedVersionId,
          resolvedVersion: delta.resolvedVersion ?? node.resolvedVersion,
          startedAt: delta.startedAt ?? node.startedAt,
          completedAt: delta.completedAt ?? node.completedAt,
          failedAt: delta.failedAt ?? node.failedAt,
          failureMessage: delta.failureMessage ?? null,
        };
      }),
    };
  });
  private readonly processNodeViewByCanvasId = computed(() =>
    new Map(
      (this.selectedProcess()?.nodes ?? []).map((node, index) => [index + 1, node] as const),
    ),
  );
  private readonly processCanvasNodeIdByInstanceId = computed(() =>
    new Map(
      (this.selectedProcess()?.nodes ?? []).map((node, index) => [node.instanceId, index + 1] as const),
    ),
  );
  protected readonly selectedProcessCanvasNodeId = computed(() => {
    const selectedStepInstanceId = this.selectedStepInstanceId();
    if (!selectedStepInstanceId) {
      return null;
    }

    return this.processCanvasNodeIdByInstanceId().get(selectedStepInstanceId) ?? null;
  });
  protected readonly selectedProcessStepNode = computed(() => {
    const selectedStepInstanceId = this.selectedStepInstanceId();
    if (!selectedStepInstanceId) {
      return null;
    }

    return this.selectedProcess()?.nodes.find((node) => node.instanceId === selectedStepInstanceId) ?? null;
  });
  protected readonly stepDrawerOpen = computed(() => this.selectedStepInstanceId() !== null);
  private readonly selectedStepState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => {
        const process = this.selectedProcess();
        const blueprintNodeId = this.selectedStepInstanceId();
        if (!process || !blueprintNodeId) {
          return null;
        }

        return {
          blueprintDefinitionId: process.blueprintDefinitionId,
          processId: process.processId,
          blueprintNodeId,
        };
      })).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintDefinitionId === right?.blueprintDefinitionId &&
          left?.processId === right?.processId &&
          left?.blueprintNodeId === right?.blueprintNodeId,
        ),
      ),
      ({ blueprintDefinitionId, processId, blueprintNodeId }) =>
        this.blueprintService.getProcessStepDetail(
          blueprintDefinitionId,
          processId,
          blueprintNodeId,
        ),
      'Unable to load the selected step.',
    ),
    { initialValue: idleRequestState<ProcessStepDetailViewModel>() },
  );
  protected readonly stepDetailLoading = computed(() => this.selectedStepState().status === 'loading');
  protected readonly selectedStepDetail = computed<ProcessStepDetailViewModel | null>(() => {
    const state = this.selectedStepState();
    return isReadyRequestState(state) ? state.data : null;
  });
  protected readonly stepDetailError = computed(() => {
    const state = this.selectedStepState();
    return isErrorRequestState(state) ? state.message : null;
  });
  protected readonly selectedStepTitle = computed(() =>
    this.selectedStepDetail()?.name
    ?? this.selectedProcessStepNode()?.name
    ?? 'Step details',
  );
  protected readonly selectedStepStatus = computed(() =>
    this.selectedStepDetail()?.status
    ?? this.selectedProcessStepNode()?.status
    ?? 'NotExecuted',
  );
  protected readonly selectedStepResolvedVersion = computed(() =>
    this.selectedStepDetail()?.resolvedVersion
    ?? this.selectedProcessStepNode()?.resolvedVersion
    ?? null,
  );
  protected readonly selectedStepStartedAt = computed(() =>
    this.selectedStepDetail()?.startedAt
    ?? this.selectedProcessStepNode()?.startedAt
    ?? null,
  );
  protected readonly selectedStepEndedAt = computed(() =>
    this.selectedStepDetail()?.endedAt
    ?? this.selectedProcessStepNode()?.failedAt
    ?? this.selectedProcessStepNode()?.completedAt
    ?? null,
  );
  protected readonly selectedStepDurationMs = computed(() =>
    this.selectedStepDetail()?.durationMs ?? null,
  );
  protected readonly selectedStepFailureMessage = computed(() =>
    this.selectedStepDetail()?.failureMessage
    ?? this.selectedProcessStepNode()?.failureMessage
    ?? null,
  );
  protected readonly selectedStepInputs = computed(() => this.selectedStepDetail()?.inputs ?? null);
  protected readonly selectedStepOutputs = computed(() => this.selectedStepDetail()?.outputs ?? null);
  protected readonly selectedStepIncomingMappings = computed(() => this.selectedStepDetail()?.incomingMappings ?? []);
  protected readonly selectedStepDesignTimeOverrides = computed(() =>
    this.selectedStepDetail()?.designTimeInputOverrides ?? null,
  );
  protected readonly selectedStepRuntimeOverrides = computed(() =>
    this.selectedStepDetail()?.runtimeOverrides ?? null,
  );
  private readonly stepLogHistoryRequest = computed<StepLogHistoryRequest | null>(() => {
    const process = this.selectedProcess();
    const blueprintNodeId = this.selectedStepInstanceId();
    if (!process || !blueprintNodeId || this.stepInspectorTab() !== 'logs') {
      return null;
    }

    const startedAt = this.selectedStepStartedAt() ?? process.createdAt;
    const status = this.selectedStepStatus();

    return {
      blueprintDefinitionId: process.blueprintDefinitionId,
      processId: process.processId,
      blueprintNodeId,
      from: startedAt,
      to: status === 'InProgress' ? null : this.selectedStepEndedAt() ?? process.updatedAt ?? null,
    };
  });
  private readonly stepLogHistoryState = toSignal(
    createRequestStateStream(
      toObservable(this.stepLogHistoryRequest).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintDefinitionId === right?.blueprintDefinitionId &&
          left?.processId === right?.processId &&
          left?.blueprintNodeId === right?.blueprintNodeId &&
          left?.from === right?.from &&
          left?.to === right?.to,
        ),
      ),
      (request) => this.observabilityService.getProcessStepLogs(
        request.blueprintDefinitionId,
        request.processId,
        request.blueprintNodeId,
        {
          from: request.from,
          to: request.to,
          limit: 400,
        },
      ),
      'Unable to load logs for that step.',
    ),
    { initialValue: idleRequestState<ProcessStepLogBatchViewModel>() },
  );
  protected readonly stepLogsLoading = computed(() => this.stepLogHistoryState().status === 'loading');
  protected readonly stepLogsError = computed(() => {
    const state = this.stepLogHistoryState();
    return isErrorRequestState(state) ? state.message : null;
  });
  protected readonly stepLogs = computed<NodeLogEntryViewModel[]>(() => {
    const historyState = this.stepLogHistoryState();
    const historyEntries = isReadyRequestState(historyState)
      ? historyState.data.entries
      : [];
    const entriesByCursor = new Map<string, NodeLogEntryViewModel>();

    for (const entry of historyEntries) {
      entriesByCursor.set(entry.cursor, entry);
    }

    for (const entry of this.liveStepLogs()) {
      entriesByCursor.set(entry.cursor, entry);
    }

      return [...entriesByCursor.values()]
      .sort((left, right) => Date.parse(left.timestamp) - Date.parse(right.timestamp));
  });
  protected readonly stepLogCount = computed(() => this.stepLogs().length);
  private readonly stepLogStreamRequest = computed(() => {
    const request = this.stepLogHistoryRequest();
    const historyState = this.stepLogHistoryState();
    if (!request || this.selectedStepStatus() !== 'InProgress' || historyState.status !== 'ready') {
      return null;
    }

      return {
        ...request,
        cursor: historyState.data.nextCursor ?? historyState.data.entries.at(-1)?.cursor ?? null,
      };
  });
  protected readonly zoomPercent = computed(() => this.canvasViewport()?.zoomPercent() ?? 100);
  protected readonly selectedProcessGrafanaLogsUrl = computed(() => {
    const process = this.selectedProcess();
    if (!process) {
      return this.runtimeClientConfig.grafanaBaseUrl;
    }

    const left = encodeURIComponent(JSON.stringify({
      datasource: 'loki',
      queries: [
        {
          refId: 'A',
          expr: `{process_id="${process.processId}"}`,
        },
      ],
      range: {
        from: process.createdAt,
        to: process.updatedAt,
      },
    }));

    return `${this.runtimeClientConfig.grafanaBaseUrl}/explore?orgId=1&left=${left}`;
  });
  protected readonly selectedProcessGrafanaTraceUrl = computed(() => {
    const process = this.selectedProcess();
    if (!process) {
      return this.runtimeClientConfig.grafanaBaseUrl;
    }

    const left = encodeURIComponent(JSON.stringify({
      datasource: 'tempo',
      queries: [
        {
          refId: 'A',
          query: process.processId,
        },
      ],
      range: {
        from: 'now-1h',
        to: 'now',
      },
    }));

    return `${this.runtimeClientConfig.grafanaBaseUrl}/explore?orgId=1&left=${left}`;
  });
  protected readonly historyPaneSummary = computed(() => {
    if (this.historyPanelMode() === 'processes') {
      return this.selectedBlueprintProcessCountLabel();
    }

    const count = this.blueprintActivityTotalCount();
    if (count === 0) {
      return 'Recent process activity';
    }

    return count === 1
      ? '1 blueprint with activity'
      : `${count} blueprints with activity`;
  });
  protected readonly selectedBlueprintTitle = computed(() =>
    this.selectedBlueprint()?.name
    ?? (this.selectedBlueprintId() ? `Blueprint ${this.selectedBlueprintId()}` : 'Process History'),
  );
  protected readonly selectedBlueprintProcessCountLabel = computed(() => {
    const count = this.processHistoryRows().length;
    if (count === 0) {
      return 'No processes loaded yet';
    }

    if (this.processHistoryHasMore()) {
      return `${count}+ loaded processes`;
    }

    return count === 1 ? '1 process' : `${count} processes`;
  });
  protected readonly errorMessage = computed(() => {
    const blueprintsState = this.blueprintsState();
    if (isErrorRequestState(blueprintsState)) {
      return blueprintsState.message;
    }

    if (this.historyPanelMode() === 'blueprints' && this.blueprintActivityError()) {
      return this.blueprintActivityError();
    }

    if (this.historyPanelMode() === 'processes' && this.processHistoryError()) {
      return this.processHistoryError();
    }

    const selectedProcessState = this.selectedProcessState();
    if (isErrorRequestState(selectedProcessState)) {
      return selectedProcessState.message;
    }

    return null;
  });

  protected readonly processNodes = computed(() => {
    const detail = this.selectedProcess();
    if (!detail) {
      return [];
    }

    return detail.nodes.map((node, index) => new NodeModel(
      index + 1,
      node.taskDefinitionId,
      node.allowedMajor,
      node.allowedMinor,
      node.resolvedVersionId,
      node.resolvedVersion ?? null,
      node.name,
      node.position.x,
      node.position.y,
      320,
      Math.max(220, 168 + Math.max(node.outputChannelNames.length, 1) * 34),
      node.inputChannelName ?? null,
      node.outputChannelNames,
      false,
      node.status,
      node.failureMessage ?? null,
    ));
  });

  protected readonly processConnections = computed(() => {
    const detail = this.selectedProcess();
    const nodes = this.processNodes();
    if (!detail || nodes.length === 0) {
      return [];
    }

    const nodeByInstanceId = new Map(detail.nodes.map((node, index) => [node.instanceId, nodes[index]] as const));
    return detail.nodes.flatMap((node) => {
      const fromNode = nodeByInstanceId.get(node.instanceId);
      if (!fromNode) {
        return [];
      }

      return node.outgoingConnections.flatMap((connection) => {
        const toNode = nodeByInstanceId.get(connection.blueprintNodeId);
        if (!toNode) {
          return [];
        }

        return [new ConnectionModel(
          fromNode,
          toNode,
          connection.anchor,
          connection.side,
          connection.position,
          crypto.randomUUID(),
          connection.propertyMappings,
        )];
      });
    });
  });

  constructor() {
    effect(() => {
      this.processRealtime.setSubscriptions(
        this.selectedBlueprintId() ?? this.routeSelection().blueprintId,
        this.selectedProcessId()
        ?? this.preferredProcessId()
        ?? this.routeSelection().processId
        ?? this.activeProcessId(),
      );
    });

    effect(() => {
      const process = this.fetchedProcess();
      if (!process) {
        return;
      }

      this.centerOnStartingNode(process);
    });

    effect((onCleanup) => {
      const request = this.stepLogStreamRequest();
      this.stopStepLogStream();

      if (!request) {
        return;
      }

      const eventSource = this.observabilityService.createProcessStepLogStream(
        request.blueprintDefinitionId,
        request.processId,
        request.blueprintNodeId,
        {
          from: request.from,
          cursor: request.cursor,
        },
      );

      eventSource.addEventListener('logBatch', (event) => {
        const messageEvent = event as MessageEvent<string>;
        const batch = JSON.parse(messageEvent.data) as ProcessStepLogBatchViewModel;
        this.appendStepLogEntries(batch.entries);
        this.stepLogStreamConnected.set(true);
      });

      eventSource.onerror = () => {
        this.stepLogStreamConnected.set(false);
      };

      this.stepLogEventSource = eventSource;

      onCleanup(() => {
        eventSource.close();
        if (this.stepLogEventSource === eventSource) {
          this.stepLogEventSource = null;
        }
        this.stepLogStreamConnected.set(false);
      });
    });

    toObservable(this.blueprintSearchQuery)
      .pipe(
        tap(() => {
          this.blueprintActivityLoading.set(true);
          this.blueprintActivityLoadingMore.set(false);
          this.blueprintActivityError.set(null);
          this.blueprintActivityRows.set([]);
          this.blueprintActivityPage.set(1);
          this.blueprintActivityTotalCount.set(0);
          this.blueprintActivityHasMore.set(false);
        }),
        switchMap((query) => this.blueprintService.getProcessBlueprintActivities(query, 1, 10).pipe(
          map((page) => ({ page, error: null as string | null })),
          catchError(() => of({
            page: null,
            error: 'Unable to load blueprint activity.',
          })),
        )),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ page, error }) => {
        this.blueprintActivityLoading.set(false);

        if (error || !page) {
          this.blueprintActivityError.set(error ?? 'Unable to load blueprint activity.');
          return;
        }

        this.blueprintActivityRows.set(page.items);
        this.blueprintActivityPage.set(page.page);
        this.blueprintActivityTotalCount.set(page.totalCount);
        this.blueprintActivityHasMore.set(page.hasMore);
      });

    toObservable(computed(() => {
      const blueprintId = this.selectedBlueprintId();
      if (!blueprintId) {
        return null;
      }

      return {
        blueprintId,
        refreshVersion: this.refreshVersion(),
      };
    }))
      .pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintId === right?.blueprintId &&
          left?.refreshVersion === right?.refreshVersion,
        ),
        tap((request) => {
          this.processHistoryBaseRows.set([]);
          this.processHistoryNextCursor.set(null);
          this.processHistoryHasMoreState.set(false);
          this.processHistoryError.set(null);
          this.processHistoryLoadingMore.set(false);
          this.processHistoryLoading.set(request !== null);
        }),
        switchMap((request) => {
          if (!request) {
            return of({
              page: null,
              error: null as string | null,
            });
          }

          return this.blueprintService.getBlueprintProcessHistory(request.blueprintId, 25).pipe(
            map((page) => ({ page, error: null as string | null })),
            catchError(() => of({
              page: null,
              error: 'Unable to load process history for the selected blueprint.',
            })),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ page, error }) => {
        this.processHistoryLoading.set(false);

        if (error) {
          this.processHistoryError.set(error);
          return;
        }

        if (!page) {
          return;
        }

        this.processHistoryBaseRows.set(page.items);
        this.processHistoryNextCursor.set(page.nextCursor ?? null);
        this.processHistoryHasMoreState.set(page.hasMore);
      });

    toObservable(this.routeSelection)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((selection) => {
        this.applyBlueprintSelection(
          selection.blueprintId,
          selection.processId,
          selection.blueprintId ? 'processes' : 'blueprints',
        );
      });

    this.processRealtime.summaryUpdates$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((updates) => this.applyRealtimeSummaryUpdates(updates));

    this.processRealtime.nodeUpdates$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((updates) => this.applyRealtimeNodeUpdates(updates));

    this.processRealtime.reconnected$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.refreshVersion.update((current) => current + 1);
      });

    toObservable(this.activeProcessId)
      .pipe(
        distinctUntilChanged(),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.closeProcessInfo();
        this.closeStepDrawer();
      });

    toObservable(this.stepLogHistoryRequest)
      .pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintDefinitionId === right?.blueprintDefinitionId &&
          left?.processId === right?.processId &&
          left?.blueprintNodeId === right?.blueprintNodeId &&
          left?.from === right?.from &&
          left?.to === right?.to,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((request) => {
        this.liveStepLogs.set([]);
        if (!request) {
          this.stopStepLogStream();
        }
      });

    this.processRealtime.connect();
    this.destroyRef.onDestroy(() => {
      this.stopStepLogStream();
      void this.processRealtime.disconnect();
    });
  }

  protected toggleHistoryPanel(): void {
    this.historyCollapsed.update((collapsed) => !collapsed);
  }

  protected selectBlueprintActivity(activity: BlueprintProcessActivityViewModel): void {
    this.applyBlueprintSelection(activity.blueprintDefinitionId, null, 'processes');
  }

  protected returnToBlueprintPicker(): void {
    this.historyPanelMode.set('blueprints');
  }

  protected async loadMoreBlueprintActivities(): Promise<void> {
    if (!this.blueprintActivityHasMore() || this.blueprintActivityLoadingMore()) {
      return;
    }

    const query = this.blueprintSearchQuery();
    const nextPage = this.blueprintActivityPage() + 1;
    this.blueprintActivityLoadingMore.set(true);
    this.blueprintActivityError.set(null);

    try {
      const response = await firstValueFrom(
        this.blueprintService.getProcessBlueprintActivities(query, nextPage, 10),
      );

      if (query !== this.blueprintSearchQuery() || nextPage <= this.blueprintActivityPage()) {
        return;
      }

      this.blueprintActivityRows.update((currentRows) =>
        this.mergeBlueprintActivities(currentRows, response.items),
      );
      this.blueprintActivityPage.set(response.page);
      this.blueprintActivityTotalCount.set(response.totalCount);
      this.blueprintActivityHasMore.set(response.hasMore);
    } catch {
      if (query === this.blueprintSearchQuery()) {
        this.blueprintActivityError.set('Unable to load more blueprints.');
      }
    } finally {
      this.blueprintActivityLoadingMore.set(false);
    }
  }

  protected selectProcess(process: BlueprintProcessHistoryEntryViewModel): void {
    this.closeProcessInfo();
    this.selectedProcessId.set(process.processId);
    this.preferredProcessId.set(null);
  }

  protected onReadonlyNodeActivated(nodeId: number): void {
    const selectedNode = this.processNodeViewByCanvasId().get(nodeId);
    if (!selectedNode) {
      return;
    }

    if (this.selectedStepInstanceId() !== selectedNode.instanceId) {
      this.stepInspectorTab.set('inputs');
    }

    this.selectedStepInstanceId.set(selectedNode.instanceId);
  }

  protected closeStepDrawer(): void {
    this.selectedStepInstanceId.set(null);
    this.stepInspectorTab.set('inputs');
    this.liveStepLogs.set([]);
    this.stopStepLogStream();
  }

  protected toggleProcessInfo(event: MouseEvent): void {
    event.stopPropagation();
    this.processInfoOpen.update((open) => !open);
  }

  protected closeProcessInfo(): void {
    this.processInfoOpen.set(false);
  }

  protected selectStepInspectorTab(tab: StepInspectorTab): void {
    this.stepInspectorTab.set(tab);
  }

  protected stepTabIndex(): number {
    switch (this.stepInspectorTab()) {
      case 'outputs':
        return 1;
      case 'dataflow':
        return 2;
      case 'logs':
        return 3;
      default:
        return 0;
    }
  }

  protected onStepTabIndexChange(index: number): void {
    switch (index) {
      case 1:
        this.stepInspectorTab.set('outputs');
        break;
      case 2:
        this.stepInspectorTab.set('dataflow');
        break;
      case 3:
        this.stepInspectorTab.set('logs');
        break;
      default:
        this.stepInspectorTab.set('inputs');
        break;
    }
  }

  protected async loadMoreProcessHistory(): Promise<void> {
    const blueprintId = this.selectedBlueprintId();
    const cursor = this.processHistoryNextCursor();

    if (!blueprintId || !cursor || this.processHistoryLoadingMore()) {
      return;
    }

    this.processHistoryLoadingMore.set(true);
    this.processHistoryError.set(null);

    try {
      const response = await firstValueFrom(
        this.blueprintService.getBlueprintProcessHistory(blueprintId, 25, cursor),
      );

      if (blueprintId !== this.selectedBlueprintId() || cursor !== this.processHistoryNextCursor()) {
        return;
      }

      this.processHistoryBaseRows.update((currentRows) =>
        this.mergeProcessHistoryRows(currentRows, response.items),
      );
      this.processHistoryNextCursor.set(response.nextCursor ?? null);
      this.processHistoryHasMoreState.set(response.hasMore);
    } catch {
      if (blueprintId === this.selectedBlueprintId()) {
        this.processHistoryError.set('Unable to load more processes.');
      }
    } finally {
      this.processHistoryLoadingMore.set(false);
    }
  }

  protected resetView(): void {
    this.canvasViewport()?.resetView();
  }

  protected zoomIn(): void {
    this.canvasViewport()?.zoomIn();
  }

  protected zoomOut(): void {
    this.canvasViewport()?.zoomOut();
  }

  protected statusLabel(status: string): string {
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
        return 'Not Executed';
    }
  }

  protected statusIcon(status: string): string {
    switch (status) {
      case 'Completed':
        return 'check_circle';
      case 'InProgress':
        return 'hourglass_top';
      case 'Failed':
        return 'error';
      case 'Pending':
        return 'schedule';
      default:
        return 'radio_button_unchecked';
    }
  }

  protected stepLogLabel(entry: NodeLogEntryViewModel): string {
    return entry.serviceName ?? 'Step';
  }

  protected blueprintActivityCountLabel(activity: BlueprintProcessActivityViewModel): string {
    return activity.recentProcessCount === 1
      ? '1 recent process'
      : `${activity.recentProcessCount} recent processes`;
  }

  protected processIdLabel(process: BlueprintProcessHistoryEntryViewModel): string {
    if (process.processId.length <= 14) {
      return process.processId;
    }

    return `${process.processId.slice(0, 8)}...${process.processId.slice(-4)}`;
  }

  protected trackBlueprintActivity(
    _: number,
    activity: BlueprintProcessActivityViewModel,
  ): number {
    return activity.blueprintDefinitionId;
  }

  protected trackProcess(
    _: number,
    process: BlueprintProcessHistoryEntryViewModel,
  ): string {
    return process.processId;
  }

  protected trackStepLog(_: number, entry: NodeLogEntryViewModel): string {
    return entry.cursor;
  }

  protected trackIncomingMapping(_: number, mapping: ProcessStepDetailViewModel['incomingMappings'][number]): string {
    return `${mapping.sourceBlueprintNodeId}:${mapping.channelName ?? 'default'}`;
  }

  protected hasStructuredPayload(payload: unknown | null | undefined): boolean {
    return payload !== null && payload !== undefined;
  }

  protected formatStructuredPayload(payload: unknown | null | undefined): string {
    if (!this.hasStructuredPayload(payload)) {
      return '';
    }

    return JSON.stringify(payload, null, 2);
  }

  protected formatDuration(durationMs: number | null | undefined): string {
    if (durationMs === null || durationMs === undefined || Number.isNaN(durationMs)) {
      return 'Not available';
    }

    if (durationMs < 1_000) {
      return `${Math.round(durationMs)}ms`;
    }

    const totalSeconds = durationMs / 1_000;
    if (totalSeconds < 60) {
      return `${totalSeconds.toFixed(totalSeconds < 10 ? 1 : 0)}s`;
    }

    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}m ${seconds.toFixed(seconds < 10 ? 1 : 0)}s`;
  }

  @HostListener('document:click', ['$event'])
  protected onDocumentClick(event: MouseEvent): void {
    if (!this.processInfoOpen()) {
      return;
    }

    const target = event.target;
    if (!(target instanceof Node)) {
      this.closeProcessInfo();
      return;
    }

    const infoButton = this.processInfoButton()?.nativeElement;
    if (infoButton?.contains(target)) {
      return;
    }

    const infoPopover = this.processInfoPopover()?.nativeElement;
    if (infoPopover?.contains(target)) {
      return;
    }

    this.closeProcessInfo();
  }

  @HostListener('document:keydown.escape')
  protected onEscapeKey(): void {
    this.closeProcessInfo();
  }

  private applyBlueprintSelection(
    blueprintId: number | null,
    preferredProcessId: string | null,
    historyPanelMode: HistoryPanelMode,
  ): void {
    this.closeProcessInfo();
    this.selectedBlueprintId.set(blueprintId);
    this.preferredProcessId.set(preferredProcessId);
    this.historyPanelMode.set(historyPanelMode);
    this.selectedProcessId.set(null);
    this.historyCollapsed.set(false);
    this.closeStepDrawer();
    this.realtimeSummaryOverrides.set(new Map());
    this.realtimeNodeOverrides.set(new Map());
  }

  private applyRealtimeSummaryUpdates(updates: ProcessSummaryUpdatedEvent[]): void {
    const selectedBlueprintId = this.selectedBlueprintId();
    if (!selectedBlueprintId) {
      return;
    }

    const relevantUpdates = updates.filter((update) => update.blueprintDefinitionId === selectedBlueprintId);
    if (relevantUpdates.length === 0) {
      return;
    }

    this.realtimeSummaryOverrides.update((currentOverrides) => {
      const nextOverrides = new Map(currentOverrides);
      for (const update of relevantUpdates) {
        nextOverrides.set(update.processId, update);
      }
      return nextOverrides;
    });
  }

  private applyRealtimeNodeUpdates(updates: ProcessStepsUpdatedEvent[]): void {
    const activeProcessId = this.activeProcessId();
    if (!activeProcessId) {
      return;
    }

    const relevantUpdates = updates.filter((update) => update.processId === activeProcessId);
    if (relevantUpdates.length === 0) {
      return;
    }

    this.realtimeNodeOverrides.update((currentOverrides) => {
      const nextOverrides = new Map(currentOverrides);
      for (const update of relevantUpdates) {
        for (const node of update.nodes) {
          nextOverrides.set(node.instanceId, node);
        }
      }
      return nextOverrides;
    });
  }

  private centerOnStartingNode(detail: ProcessDetailViewModel): void {
    const nodeIndex = detail.nodes.findIndex((node) => node.instanceId === detail.startingBlueprintNodeId);
    if (nodeIndex < 0) {
      return;
    }

    queueMicrotask(() => this.canvasViewport()?.centerOnNode(nodeIndex + 1));
  }

  private appendStepLogEntries(entries: NodeLogEntryViewModel[]): void {
    if (entries.length === 0) {
      return;
    }

    this.liveStepLogs.update((currentEntries) => {
      const entriesByCursor = new Map(currentEntries.map((entry) => [entry.cursor, entry] as const));
      for (const entry of entries) {
        entriesByCursor.set(entry.cursor, entry);
      }

      return [...entriesByCursor.values()]
        .sort((left, right) => Date.parse(left.timestamp) - Date.parse(right.timestamp));
    });
  }

  private stopStepLogStream(): void {
    this.stepLogEventSource?.close();
    this.stepLogEventSource = null;
    this.stepLogStreamConnected.set(false);
  }

  private mergeBlueprintActivities(
    currentRows: BlueprintProcessActivityViewModel[],
    nextRows: BlueprintProcessActivityViewModel[],
  ): BlueprintProcessActivityViewModel[] {
    const rowsByDefinitionId = new Map<number, BlueprintProcessActivityViewModel>(
      currentRows.map((row) => [row.blueprintDefinitionId, row] as const),
    );

    for (const row of nextRows) {
      rowsByDefinitionId.set(row.blueprintDefinitionId, row);
    }

    return [...rowsByDefinitionId.values()];
  }

  private mergeProcessHistoryRows(
    currentRows: BlueprintProcessHistoryEntryViewModel[],
    nextRows: BlueprintProcessHistoryEntryViewModel[],
  ): BlueprintProcessHistoryEntryViewModel[] {
    const rowsByProcessId = new Map<string, BlueprintProcessHistoryEntryViewModel>(
      currentRows.map((row) => [row.processId, row] as const),
    );

    for (const row of nextRows) {
      rowsByProcessId.set(row.processId, row);
    }

    return [...rowsByProcessId.values()]
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
  }
}

