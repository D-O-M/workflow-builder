import '@angular/compiler';
import {
  EnvironmentInjector,
  Provider,
  createEnvironmentInjector,
  runInInjectionContext,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵEffectScheduler as EffectScheduler,
} from '@angular/core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DashboardPageComponent } from './dashboard-page.component';
import {
  BlueprintEditorService,
  BlueprintViewModel,
} from '../domain/blueprint-editor.service';
import {
  NodeObservabilitySummaryItemViewModel,
  NodeObservabilitySummaryViewModel,
  ObservabilityService,
  ObservabilityWindow,
  ProcessObservabilitySummaryViewModel,
  RecentProcessViewModel,
  RecentProcessesViewModel,
} from '../domain/observability.service';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';

function createBlueprints(): BlueprintViewModel[] {
  return [
    {
      id: 3,
      name: 'Showcase Municipal Readiness Advisor',
      description: '',
      versions: [],
    },
  ];
}

function createProcessSummary(
  overrides: Partial<ProcessObservabilitySummaryViewModel> = {},
): ProcessObservabilitySummaryViewModel {
  return {
    window: '1h',
    from: '2026-04-28T11:00:00Z',
    to: '2026-04-28T12:00:00Z',
    successfulCount: 1,
    failedCount: 1,
    totalCount: 2,
    ...overrides,
  };
}

function createRecentProcesses(window: ObservabilityWindow): RecentProcessesViewModel {
  return {
    window,
    from: '2026-04-28T11:00:00Z',
    to: '2026-04-28T12:00:00Z',
    processes: [],
  };
}

function createNodeSummary(window: ObservabilityWindow): NodeObservabilitySummaryViewModel {
  return {
    window,
    from: '2026-04-28T11:00:00Z',
    to: '2026-04-28T12:00:00Z',
    blueprintDefinitionId: 3,
    blueprintName: 'Showcase Municipal Readiness Advisor',
    blueprintVersionId: 301,
    blueprintVersionNumber: 1,
    nodes: [],
  };
}

function createStepNode(
  overrides: Partial<NodeObservabilitySummaryItemViewModel> = {},
): NodeObservabilitySummaryItemViewModel {
  return {
    blueprintNodeId: overrides.blueprintNodeId ?? crypto.randomUUID(),
    taskName: overrides.taskName ?? 'Step',
    taskDefinitionId: overrides.taskDefinitionId ?? 1,
    successfulCount: overrides.successfulCount ?? 0,
    failedCount: overrides.failedCount ?? 0,
    totalCount: overrides.totalCount ?? 0,
    successRate: overrides.successRate ?? 0,
  };
}

function createRecentProcess(
  overrides: Partial<RecentProcessViewModel> = {},
): RecentProcessViewModel {
  return {
    processId: overrides.processId ?? 'process-123',
    blueprintDefinitionId: overrides.blueprintDefinitionId ?? 3,
    blueprintName: overrides.blueprintName ?? 'Showcase Municipal Readiness Advisor',
    blueprintVersionId: overrides.blueprintVersionId ?? 301,
    blueprintVersionNumber: overrides.blueprintVersionNumber ?? 1,
    status: overrides.status ?? 'Completed',
    occurredAt: overrides.occurredAt ?? '2026-04-28T12:00:00Z',
    failureMessage: overrides.failureMessage,
  };
}

describe('DashboardPageComponent', () => {
  let injector: EnvironmentInjector | undefined;
  let currentProcessSummary: ProcessObservabilitySummaryViewModel;

  afterEach(() => {
    injector?.destroy();
    injector = undefined;
  });

  async function createComponent(
    summary: ProcessObservabilitySummaryViewModel = createProcessSummary(),
  ): Promise<DashboardPageComponent> {
    injector?.destroy();
    injector = undefined;
    currentProcessSummary = summary;

    const observabilityService = {
      getProcessSummary: vi.fn((window: ObservabilityWindow) => of({
        ...currentProcessSummary,
        window,
      })),
      getRecentProcesses: vi.fn((window: ObservabilityWindow) => of(createRecentProcesses(window))),
      getNodeSummary: vi.fn((window: ObservabilityWindow) => of(createNodeSummary(window))),
    };

    const providers: Provider[] = [
      {
        provide: BlueprintEditorService,
        useValue: {
          getBlueprints: vi.fn(() => of(createBlueprints())),
        },
      },
      { provide: ObservabilityService, useValue: observabilityService },
      {
        provide: RuntimeClientConfigService,
        useValue: {
          grafanaBaseUrl: 'http://grafana.local',
        },
      },
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
    const component = runInInjectionContext(injector, () => new DashboardPageComponent());
    await flushSignals();
    return component;
  }

  async function flushSignals(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
  }

  function grafanaLogsExpression(component: DashboardPageComponent): string {
    const url = new URL((component as any).grafanaLogsUrl());
    const left = url.searchParams.get('left');
    expect(left).not.toBeNull();
    const payload = JSON.parse(left!) as { queries: Array<{ expr: string }> };
    return payload.queries[0].expr;
  }

  function setRenderedProcessSummary(
    component: DashboardPageComponent,
    summary: ProcessObservabilitySummaryViewModel,
  ): void {
    Object.defineProperty(component as any, 'processSummary', {
      value: () => summary,
      configurable: true,
    });
  }

  function setRenderedNodeSummary(
    component: DashboardPageComponent,
    nodes: NodeObservabilitySummaryItemViewModel[],
  ): void {
    Object.defineProperty(component as any, 'nodeSummary', {
      value: () => ({
        ...createNodeSummary('1h'),
        nodes,
      }),
      configurable: true,
    });
  }

  it('maps recent process statuses to labels and styled status chip classes', async () => {
    const component = await createComponent();

    expect((component as any).recentProcessStatusLabel('Completed')).toBe('Succeeded');
    expect((component as any).recentProcessStatusClass('Completed')).toBe('status-chip--success');
    expect((component as any).recentProcessStatusLabel('Failed')).toBe('Failed');
    expect((component as any).recentProcessStatusClass('Failed')).toBe('status-chip--failed');
    expect((component as any).recentProcessStatusLabel('InProgress')).toBe('Running');
    expect((component as any).recentProcessStatusClass('InProgress')).toBe('status-chip--running');
    expect((component as any).recentProcessStatusLabel('Pending')).toBe('Pending');
    expect((component as any).recentProcessStatusClass('Pending')).toBe('status-chip--pending');
    expect((component as any).recentProcessStatusLabel('Unexpected')).toBe('Unknown');
    expect((component as any).recentProcessStatusClass('Unexpected')).toBe('status-chip--neutral');
  });

  it('uses neutral rings for zero totals and success/failure rings for non-zero totals', async () => {
    const component = await createComponent();

    const emptyBackground = (component as any).donutBackground(0, 0) as string;
    const splitBackground = (component as any).donutBackground(2, 2) as string;

    expect(emptyBackground).toContain('var(--app-surface-muted) 0 100%');
    expect(emptyBackground).not.toContain('var(--app-status-danger)');
    expect(splitBackground).toContain('var(--app-status-success) 0 50%');
    expect(splitBackground).toContain('var(--app-status-danger) 50% 100%');
  });

  it('ranks step reliability by failing, degraded, healthy, then idle', async () => {
    const component = await createComponent();
    setRenderedNodeSummary(component, [
      createStepNode({
        blueprintNodeId: 'idle',
        taskName: 'Idle Step',
      }),
      createStepNode({
        blueprintNodeId: 'healthy',
        taskName: 'Healthy Step',
        successfulCount: 3,
        failedCount: 0,
        totalCount: 3,
        successRate: 1,
      }),
      createStepNode({
        blueprintNodeId: 'degraded',
        taskName: 'Degraded Step',
        successfulCount: 2,
        failedCount: 1,
        totalCount: 3,
        successRate: 0.66,
      }),
      createStepNode({
        blueprintNodeId: 'failing',
        taskName: 'Failing Step',
        successfulCount: 0,
        failedCount: 2,
        totalCount: 2,
        successRate: 0,
      }),
    ]);

    expect((component as any).rankedStepNodes().map((node: NodeObservabilitySummaryItemViewModel) => node.taskName)).toEqual([
      'Failing Step',
      'Degraded Step',
      'Healthy Step',
      'Idle Step',
    ]);
    expect((component as any).stepReliabilitySummary()).toBe('1 failing · 1 degraded · 1 healthy · 1 idle');
  });

  it('uses concise idle labels for dense step reliability rows', async () => {
    const component = await createComponent();
    const idleNode = createStepNode();

    expect((component as any).nodeHealthLabel(idleNode)).toBe('Idle');
    expect((component as any).nodeHealthClass(idleNode)).toBe('node-health--idle');
  });

  it('returns failed process detail only when a failure message exists', async () => {
    const component = await createComponent();

    expect((component as any).failedProcessDetail(createRecentProcess({
      status: 'Failed',
      failureMessage: 'Weather lookup timed out.',
    }))).toBe('Weather lookup timed out.');
    expect((component as any).failedProcessDetail(createRecentProcess({
      status: 'Failed',
      failureMessage: '   ',
    }))).toBeNull();
    expect((component as any).failedProcessDetail(createRecentProcess({
      status: 'Completed',
      failureMessage: 'Ignored.',
    }))).toBeNull();
  });

  it('describes process health from the current process summary', async () => {
    const component = await createComponent();
    setRenderedProcessSummary(component, createProcessSummary({
      successfulCount: 0,
      failedCount: 0,
      totalCount: 0,
    }));

    expect((component as any).processHealthLabel()).toBe('No terminal processes');
    expect((component as any).processHealthClass()).toBe('dashboard-health--idle');

    setRenderedProcessSummary(component, createProcessSummary({
      successfulCount: 3,
      failedCount: 0,
      totalCount: 3,
    }));

    expect((component as any).processHealthLabel()).toBe('Healthy');
    expect((component as any).processHealthClass()).toBe('dashboard-health--success');

    setRenderedProcessSummary(component, createProcessSummary({
      successfulCount: 0,
      failedCount: 2,
      totalCount: 2,
    }));

    expect((component as any).processHealthLabel()).toBe('Failing');
    expect((component as any).processHealthClass()).toBe('dashboard-health--failed');

    setRenderedProcessSummary(component, createProcessSummary({
      successfulCount: 2,
      failedCount: 1,
      totalCount: 3,
    }));

    expect((component as any).processHealthLabel()).toBe('Mixed outcome');
    expect((component as any).processHealthClass()).toBe('dashboard-health--mixed');
  });

  it('filters history and Grafana log links by the selected blueprint', async () => {
    const component = await createComponent();

    expect((component as any).historyQueryParams()).toEqual({});
    expect(grafanaLogsExpression(component)).toBe('{service_name=~".+"}');

    (component as any).selectBlueprint('3');
    await flushSignals();

    expect((component as any).historyQueryParams()).toEqual({ blueprintId: 3 });
    expect(grafanaLogsExpression(component)).toBe('{blueprint_definition_id="3"}');
    expect(decodeURIComponent((component as any).grafanaLogsUrl())).toContain('"from":"now-1h"');
  });
});
