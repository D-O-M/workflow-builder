import '@angular/compiler';
import {
  EnvironmentInjector,
  Provider,
  createEnvironmentInjector,
  runInInjectionContext,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵEffectScheduler as EffectScheduler,
} from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { Subject, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ProcessExplorerPageComponent } from './process-explorer-page.component';
import {
  BlueprintEditorService,
  BlueprintProcessActivityViewModel,
  BlueprintProcessHistoryEntryViewModel,
  BlueprintViewModel,
  ProcessDetailViewModel,
  ProcessSearchRequest,
  ProcessStepDetailViewModel,
} from '../domain/blueprint-editor.service';
import {
  ProcessRealtimeService,
  ProcessStepsUpdatedEvent,
  ProcessSummaryUpdatedEvent,
} from '../domain/process-realtime.service';
import { ObservabilityService } from '../domain/observability.service';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';
import { TaskDefinitionsService } from '../domain/task-definitions.service';

const createdAt = '2026-04-25T09:15:00Z';
const updatedAt = '2026-04-25T09:17:30Z';

function createBlueprints(): BlueprintViewModel[] {
  return [
    {
      id: 1,
      name: 'Municipal Readiness Advisor',
      description: '',
      versions: [
        {
          blueprintDefinitionId: 1,
          blueprintVersionId: 101,
          description: null,
          version: 3,
          startingBlueprintNodeId: 'node-1',
          tasks: [],
        },
      ],
    },
  ];
}

function createBlueprintActivity(): BlueprintProcessActivityViewModel {
  return {
    blueprintDefinitionId: 1,
    blueprintName: 'Municipal Readiness Advisor',
    recentProcessCount: 3,
    latestProcessAt: updatedAt,
  };
}

function createProcessHistoryRow(overrides: Partial<BlueprintProcessHistoryEntryViewModel> = {}): BlueprintProcessHistoryEntryViewModel {
  return {
    processId: 'process-abc-123456',
    blueprintDefinitionId: 1,
    blueprintName: 'Municipal Readiness Advisor',
    blueprintVersionId: 101,
    blueprintVersionNumber: 3,
    status: 'InProgress',
    createdAt,
    updatedAt,
    ...overrides,
  };
}

function createProcessDetail(): ProcessDetailViewModel {
  return {
    processId: 'process-abc-123456',
    blueprintDefinitionId: 1,
    blueprintName: 'Municipal Readiness Advisor',
    blueprintVersionId: 101,
    blueprintVersionNumber: 3,
    status: 'InProgress',
    createdAt,
    updatedAt,
    startingBlueprintNodeId: 'node-1',
    nodes: [
      {
        instanceId: 'node-1',
        taskDefinitionId: 11,
        allowedMajor: 1,
        allowedMinor: 0,
        resolvedVersionId: 1001,
        resolvedVersion: '1.0.0',
        name: 'Town Lookup',
        status: 'Completed',
        position: { x: 100, y: 120 },
        startedAt: createdAt,
        completedAt: '2026-04-25T09:15:05Z',
        failedAt: null,
        failureMessage: null,
        inputChannelName: 'Input',
        outputChannelNames: ['Success'],
        outgoingConnections: [],
      },
      {
        instanceId: 'node-2',
        taskDefinitionId: 12,
        allowedMajor: 1,
        allowedMinor: 0,
        resolvedVersionId: 1002,
        resolvedVersion: '1.0.0',
        name: 'Weather Lookup',
        status: 'InProgress',
        position: { x: 460, y: 120 },
        startedAt: '2026-04-25T09:15:06Z',
        completedAt: null,
        failedAt: null,
        failureMessage: null,
        inputChannelName: 'Input',
        outputChannelNames: ['Success'],
        outgoingConnections: [],
      },
      {
        instanceId: 'node-3',
        taskDefinitionId: 13,
        allowedMajor: 1,
        allowedMinor: 0,
        resolvedVersionId: 1003,
        resolvedVersion: '1.0.0',
        name: 'Readiness Score',
        status: 'Pending',
        position: { x: 820, y: 120 },
        startedAt: null,
        completedAt: null,
        failedAt: null,
        failureMessage: null,
        inputChannelName: 'Input',
        outputChannelNames: ['Success'],
        outgoingConnections: [],
      },
      {
        instanceId: 'node-4',
        taskDefinitionId: 14,
        allowedMajor: 1,
        allowedMinor: 0,
        resolvedVersionId: 1004,
        resolvedVersion: '1.0.0',
        name: 'Recommendation Output',
        status: 'Failed',
        position: { x: 1180, y: 120 },
        startedAt: '2026-04-25T09:16:00Z',
        completedAt: null,
        failedAt: updatedAt,
        failureMessage: 'Output endpoint rejected the recommendation.',
        inputChannelName: 'Input',
        outputChannelNames: ['Failure'],
        outgoingConnections: [],
      },
    ],
  };
}

function createStepDetail(): ProcessStepDetailViewModel {
  return {
    blueprintNodeId: 'node-1',
    name: 'Town Lookup',
    status: 'Completed',
    resolvedVersion: '1.0.0',
    startedAt: createdAt,
    endedAt: '2026-04-25T09:15:05Z',
    durationMs: 5000,
    failureMessage: null,
    inputs: { town: 'York' },
    outputs: { latitude: 53.959, longitude: -1.081 },
    incomingMappings: [],
    designTimeInputOverrides: null,
    runtimeOverrides: null,
  };
}

describe('ProcessExplorerPageComponent', () => {
  let component: ProcessExplorerPageComponent;
  let injector: EnvironmentInjector;
  let summaryUpdates: Subject<ProcessSummaryUpdatedEvent[]>;
  let nodeUpdates: Subject<ProcessStepsUpdatedEvent[]>;
  let reconnected: Subject<void>;
  let editorService: {
    getBlueprints: ReturnType<typeof vi.fn>;
    getProcessBlueprintActivities: ReturnType<typeof vi.fn>;
    getBlueprintProcessHistory: ReturnType<typeof vi.fn>;
    getBlueprintProcess: ReturnType<typeof vi.fn>;
    getProcessStepDetail: ReturnType<typeof vi.fn>;
    searchProcesses: ReturnType<typeof vi.fn>;
  };
  let dialog: {
    open: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    summaryUpdates = new Subject<ProcessSummaryUpdatedEvent[]>();
    nodeUpdates = new Subject<ProcessStepsUpdatedEvent[]>();
    reconnected = new Subject<void>();

    editorService = {
      getBlueprints: vi.fn(() => of(createBlueprints())),
      getProcessBlueprintActivities: vi.fn(() => of({
        items: [createBlueprintActivity()],
        page: 1,
        pageSize: 10,
        totalCount: 1,
        hasMore: false,
        rankingWindowHours: 24,
      })),
      getBlueprintProcessHistory: vi.fn(() => of({
        items: [createProcessHistoryRow()],
        nextCursor: null,
        hasMore: false,
      })),
      getBlueprintProcess: vi.fn(() => of(createProcessDetail())),
      getProcessStepDetail: vi.fn(() => of(createStepDetail())),
      searchProcesses: vi.fn(() => of({
        items: [createProcessHistoryRow({ processId: 'search-process-1', status: 'Completed' })],
        nextCursor: null,
        hasMore: false,
      })),
    };

    dialog = {
      open: vi.fn(),
    };

    const providers: Provider[] = [
      { provide: BlueprintEditorService, useValue: editorService },
      {
        provide: TaskDefinitionsService,
        useValue: {
          getTaskDefinitions: () => of([]),
        },
      },
      {
        provide: ProcessRealtimeService,
        useValue: {
          summaryUpdates$: summaryUpdates.asObservable(),
          nodeUpdates$: nodeUpdates.asObservable(),
          reconnected$: reconnected.asObservable(),
          connect: vi.fn(),
          disconnect: vi.fn(() => Promise.resolve()),
          setSubscriptions: vi.fn(),
        },
      },
      {
        provide: ObservabilityService,
        useValue: {
          getProcessStepLogs: vi.fn(() => of({ entries: [], nextCursor: null })),
          createProcessStepLogStream: vi.fn(),
        },
      },
      {
        provide: RuntimeClientConfigService,
        useValue: {
          apiBaseUrl: 'http://localhost:5000',
          signalRBaseUrl: 'http://localhost:5000',
          grafanaBaseUrl: 'http://localhost:3000',
        },
      },
      {
        provide: ActivatedRoute,
        useValue: {
          queryParamMap: of(convertToParamMap({
            blueprintId: '1',
            processId: 'process-abc-123456',
          })),
        },
      },
      { provide: MatDialog, useValue: dialog },
      {
        provide: ChangeDetectionScheduler,
        useValue: {
          notify: vi.fn(),
          runningTick: false,
        },
      },
      {
        provide: EffectScheduler,
        useValue: {
          add: vi.fn(),
          schedule: vi.fn(),
          flush: vi.fn(),
          remove: vi.fn(),
        },
      },
    ];

    injector = createEnvironmentInjector(providers, null as never);
    component = runInInjectionContext(injector, () => new ProcessExplorerPageComponent());
    await flushSignals();
  });

  afterEach(() => {
    injector.destroy();
  });

  async function flushSignals(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
  }

  async function selectLoadedProcess(): Promise<void> {
    await flushSignals();
  }

  it('opens the task details drawer only after a readonly canvas node is activated', async () => {
    await selectLoadedProcess();
    (component as any).processNodeViewByCanvasId = () => new Map([
      [1, createProcessDetail().nodes[0]],
    ]);
    (component as any).selectedProcessStepNode = () => createProcessDetail().nodes[0];

    expect((component as any).stepDrawerOpen()).toBe(false);

    (component as any).onReadonlyNodeActivated(1);
    await flushSignals();

    expect((component as any).stepDrawerOpen()).toBe(true);
    expect((component as any).selectedStepTitle()).toBe('Town Lookup');

    (component as any).closeStepDrawer();

    expect((component as any).stepDrawerOpen()).toBe(false);
  });

  it('derives process status counts for the floating process pill', async () => {
    await selectLoadedProcess();

    const counts = (component as any).countStepStatuses(createProcessDetail().nodes);

    expect(counts).toEqual({
      total: 4,
      completed: 1,
      failed: 1,
      running: 1,
      pending: 1,
    });
    expect((component as any).stepCountSummary(counts)).toBe('1 running · 1 failed · 1 waiting');
  });

  it('uses compact labels and icons for dense process status presentation', () => {
    expect((component as any).compactProcessId('process-abc-123456')).toBe('process-...3456');
    expect((component as any).statusLabel('Completed')).toBe('Succeeded');
    expect((component as any).statusIcon('Completed')).toBe('check_circle');
    expect((component as any).statusIcon('NotExecuted')).toBe('radio_button_unchecked');
  });

  it('applies advanced search and switches the rail to search results', async () => {
    const request: ProcessSearchRequest = {
      blueprintDefinitionIds: [],
      processStatuses: ['Completed'],
      createdFrom: '2026-04-25T09:00:00Z',
      createdTo: '2026-04-25T09:30:00Z',
      nodeFilters: [],
      propertyFilters: [],
      limit: 25,
      cursor: null,
    };
    dialog.open.mockReturnValue({
      afterClosed: () => of(request),
    });

    await (component as any).openAdvancedSearch();
    await flushSignals();

    expect(editorService.searchProcesses).toHaveBeenCalledWith(request);
    expect((component as any).historyPanelMode()).toBe('search-results');
    expect((component as any).searchResultsRows()).toHaveLength(1);
    expect((component as any).activeSearchSummaryChips()).toContain('1 status');
  });

  it('clearing search returns to the selected blueprint process rail', async () => {
    const request: ProcessSearchRequest = {
      blueprintDefinitionIds: [],
      processStatuses: [],
      createdFrom: '2026-04-25T09:00:00Z',
      createdTo: '2026-04-25T09:30:00Z',
      nodeFilters: [],
      propertyFilters: [],
      limit: 25,
      cursor: null,
    };

    await (component as any).applyAdvancedSearch(request);
    await flushSignals();
    (component as any).selectProcess(createProcessHistoryRow({ processId: 'search-process-1' }));

    (component as any).clearAdvancedSearch();

    expect((component as any).historyPanelMode()).toBe('processes');
    expect((component as any).activeSearchRequest()).toBeNull();
  });

  it('opens and closes the history drawer without changing the selected process', async () => {
    await selectLoadedProcess();
    const selectedProcess = (component as any).selectedProcess();

    (component as any).closeHistoryDrawer();

    expect((component as any).historyDrawerOpen()).toBe(false);
    expect((component as any).selectedProcess()).toBe(selectedProcess);

    (component as any).openHistoryDrawer();

    expect((component as any).historyDrawerOpen()).toBe(true);
    expect((component as any).selectedProcess()).toBe(selectedProcess);
  });

  it('keeps the history drawer closed for the default explorer route', async () => {
    (component as any).openHistoryDrawer();

    (component as any).applyBlueprintSelection(null, null, 'blueprints');

    expect((component as any).historyDrawerOpen()).toBe(false);
    expect((component as any).historyPanelMode()).toBe('blueprints');
  });

  it('closes the history drawer after selecting a process row', async () => {
    await selectLoadedProcess();
    (component as any).openHistoryDrawer();

    (component as any).selectProcess(createProcessHistoryRow({ processId: 'process-next-456789' }));

    expect((component as any).selectedProcessId()).toBe('process-next-456789');
    expect((component as any).historyDrawerOpen()).toBe(false);
  });
});
