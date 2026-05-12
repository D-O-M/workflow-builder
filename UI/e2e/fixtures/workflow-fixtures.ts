import type {
  BlueprintProcessActivityPageViewModel,
  BlueprintProcessAcceptedViewModel,
  BlueprintProcessHistoryEntryViewModel,
  BlueprintProcessHistoryPageViewModel,
  BlueprintViewModel,
  PhasedBlueprintViewModel,
  ProcessDetailViewModel,
  ProcessSearchPageViewModel,
  ProcessStepDetailViewModel,
  ProcessSummaryViewModel,
} from '../../src/app/domain/blueprint-editor.service';
import type {
  NodeObservabilitySummaryViewModel,
  ProcessLogBatchViewModel,
  ProcessObservabilitySummaryViewModel,
  ProcessStepLogBatchViewModel,
  RecentProcessesViewModel,
} from '../../src/app/domain/observability.service';
import type { ITaskDefinition } from '../../src/app/interfaces/ITaskDefinition';

const now = '2026-05-12T10:30:00.000Z';

export const taskDefinitions: ITaskDefinition[] = [
  {
    id: 101,
    name: 'WeatherLookup',
    displayName: 'Weather Lookup',
    versions: [
      {
        id: 1001,
        version: '1.0.0',
        state: 'Active',
        description: 'Collects weather and disruption signals for a location.',
        requiredGlobalState: {
          id: 501,
          name: 'Global',
          displayName: 'Global state',
          properties: [
            { name: 'operator', kind: 'String', required: true, enumValues: [] },
            { name: 'region', kind: 'Enum', required: true, enumValues: ['EMEA', 'AMER', 'APAC'] },
          ],
        },
        inputChannel: {
          id: 601,
          name: 'Trigger',
          displayName: 'Trigger',
          properties: [
            { name: 'city', kind: 'String', required: true, enumValues: [] },
            { name: 'severity', kind: 'Enum', required: true, enumValues: ['Low', 'Medium', 'High'] },
          ],
        },
        outputChannels: [
          {
            id: 701,
            name: 'Success',
            displayName: 'Success',
            properties: [
              { name: 'forecast', kind: 'String', required: true, enumValues: [] },
              { name: 'riskScore', kind: 'Decimal', required: true, enumValues: [] },
              { name: 'alerts', kind: 'String[]', required: false, enumValues: [] },
            ],
          },
        ],
      },
      {
        id: 1002,
        version: '1.1.0',
        state: 'Active',
        description: 'Adds confidence output for downstream consumers.',
        requiredGlobalState: {
          id: 502,
          name: 'Global',
          displayName: 'Global state',
          properties: [
            { name: 'operator', kind: 'String', required: true, enumValues: [] },
            { name: 'region', kind: 'Enum', required: true, enumValues: ['EMEA', 'AMER', 'APAC'] },
          ],
        },
        inputChannel: {
          id: 602,
          name: 'Trigger',
          displayName: 'Trigger',
          properties: [
            { name: 'city', kind: 'String', required: true, enumValues: [] },
            { name: 'severity', kind: 'Enum', required: true, enumValues: ['Low', 'Medium', 'High'] },
          ],
        },
        outputChannels: [
          {
            id: 702,
            name: 'Success',
            displayName: 'Success',
            properties: [
              { name: 'forecast', kind: 'String', required: true, enumValues: [] },
              { name: 'riskScore', kind: 'Decimal', required: true, enumValues: [] },
              { name: 'confidence', kind: 'Integer', required: false, enumValues: [] },
            ],
          },
        ],
      },
    ],
  },
  {
    id: 102,
    name: 'DispatchSummary',
    displayName: 'Dispatch Summary',
    versions: [
      {
        id: 2001,
        version: '1.0.0',
        state: 'Active',
        description: 'Formats a concise operational summary.',
        requiredGlobalState: null,
        inputChannel: {
          id: 603,
          name: 'Input',
          displayName: 'Input',
          properties: [
            { name: 'forecast', kind: 'String', required: true, enumValues: [] },
            { name: 'riskScore', kind: 'Decimal', required: true, enumValues: [] },
          ],
        },
        outputChannels: [
          {
            id: 703,
            name: 'Done',
            displayName: 'Done',
            properties: [
              { name: 'summary', kind: 'String', required: true, enumValues: [] },
            ],
          },
        ],
      },
    ],
  },
  {
    id: 103,
    name: 'NotifyOperator',
    displayName: 'Notify Operator',
    versions: [
      {
        id: 3001,
        version: '1.0.0',
        state: 'Active',
        description: 'Sends a notification to an operator channel.',
        requiredGlobalState: null,
        inputChannel: {
          id: 604,
          name: 'Input',
          displayName: 'Input',
          properties: [
            { name: 'summary', kind: 'String', required: true, enumValues: [] },
          ],
        },
        outputChannels: [
          {
            id: 704,
            name: 'Done',
            displayName: 'Done',
            properties: [
              { name: 'notificationId', kind: 'Guid', required: true, enumValues: [] },
            ],
          },
        ],
      },
    ],
  },
];

export const initialBlueprints: BlueprintViewModel[] = [
  {
    id: 10,
    name: 'Airport Disruption Advisor',
    description: 'Forecasts disruption severity and dispatches an operational summary.',
    versions: [
      {
        blueprintDefinitionId: 10,
        blueprintVersionId: 100,
        version: 2,
        description: null,
        createdAt: now,
        startingBlueprintNodeId: 'node-weather',
        tasks: [
          {
            instanceId: 'node-weather',
            nodeKind: 'PluginTask',
            taskDefinitionId: 101,
            allowedMajor: 1,
            allowedMinor: 0,
            resolvedVersionId: 1001,
            resolvedVersion: '1.0.0',
            position: { x: 80, y: 80 },
            designTimeInputOverrides: {},
            outgoingConnections: [
              {
                channelName: 'Success',
                blueprintNodeId: 'node-summary',
                anchor: 1,
                side: 'left',
                position: 0.5,
                propertyMappings: [
                  { sourceProperty: 'forecast', targetProperty: 'forecast' },
                  { sourceProperty: 'riskScore', targetProperty: 'riskScore' },
                ],
              },
            ],
          },
          {
            instanceId: 'node-summary',
            nodeKind: 'PluginTask',
            taskDefinitionId: 102,
            allowedMajor: 1,
            allowedMinor: 0,
            resolvedVersionId: 2001,
            resolvedVersion: '1.0.0',
            position: { x: 520, y: 120 },
            designTimeInputOverrides: {},
            outgoingConnections: [
              {
                channelName: 'Done',
                blueprintNodeId: 'node-notify',
                anchor: 1,
                side: 'left',
                position: 0.5,
                propertyMappings: [
                  { sourceProperty: 'summary', targetProperty: 'summary' },
                ],
              },
            ],
          },
          {
            instanceId: 'node-notify',
            nodeKind: 'PluginTask',
            taskDefinitionId: 103,
            allowedMajor: 1,
            allowedMinor: 0,
            resolvedVersionId: 3001,
            resolvedVersion: '1.0.0',
            position: { x: 960, y: 150 },
            designTimeInputOverrides: {},
            outgoingConnections: [],
          },
        ],
      },
      {
        blueprintDefinitionId: 10,
        blueprintVersionId: 101,
        version: 1,
        description: null,
        createdAt: '2026-05-11T10:30:00.000Z',
        startingBlueprintNodeId: 'node-weather-v1',
        tasks: [
          {
            instanceId: 'node-weather-v1',
            nodeKind: 'PluginTask',
            taskDefinitionId: 101,
            allowedMajor: 1,
            allowedMinor: 0,
            resolvedVersionId: 1001,
            resolvedVersion: '1.0.0',
            position: { x: 120, y: 100 },
            designTimeInputOverrides: {},
            outgoingConnections: [],
          },
        ],
      },
    ],
  },
  {
    id: 11,
    name: 'Municipal Readiness Advisor',
    description: 'Prepares a public readiness summary.',
    versions: [
      {
        blueprintDefinitionId: 11,
        blueprintVersionId: 110,
        version: 1,
        description: null,
        createdAt: now,
        startingBlueprintNodeId: 'readiness-summary',
        tasks: [
          {
            instanceId: 'readiness-summary',
            nodeKind: 'PluginTask',
            taskDefinitionId: 102,
            allowedMajor: 1,
            allowedMinor: 0,
            resolvedVersionId: 2001,
            resolvedVersion: '1.0.0',
            position: { x: 220, y: 140 },
            designTimeInputOverrides: {},
            outgoingConnections: [],
          },
        ],
      },
    ],
  },
];

export const initialProcesses: BlueprintProcessHistoryEntryViewModel[] = [
  {
    processId: '11111111-1111-4111-8111-111111111111',
    blueprintDefinitionId: 10,
    blueprintName: 'Airport Disruption Advisor',
    blueprintVersionId: 100,
    blueprintVersionNumber: 2,
    status: 'Completed',
    createdAt: '2026-05-12T10:10:00.000Z',
    updatedAt: '2026-05-12T10:12:30.000Z',
  },
  {
    processId: '22222222-2222-4222-8222-222222222222',
    blueprintDefinitionId: 10,
    blueprintName: 'Airport Disruption Advisor',
    blueprintVersionId: 100,
    blueprintVersionNumber: 2,
    status: 'Failed',
    createdAt: '2026-05-12T09:30:00.000Z',
    updatedAt: '2026-05-12T09:33:00.000Z',
  },
];

export function processDetail(processId = initialProcesses[0]!.processId): ProcessDetailViewModel {
  return {
    processId,
    blueprintDefinitionId: 10,
    blueprintName: 'Airport Disruption Advisor',
    blueprintVersionId: 100,
    blueprintVersionNumber: 2,
    status: processId === initialProcesses[1]!.processId ? 'Failed' : 'Completed',
    createdAt: '2026-05-12T10:10:00.000Z',
    updatedAt: '2026-05-12T10:12:30.000Z',
    startingBlueprintNodeId: 'node-weather',
    nodes: [
      {
        instanceId: 'node-weather',
        nodeKind: 'PluginTask',
        taskDefinitionId: 101,
        allowedMajor: 1,
        allowedMinor: 0,
        resolvedVersionId: 1001,
        resolvedVersion: '1.0.0',
        name: 'Weather Lookup',
        status: 'Completed',
        position: { x: 80, y: 80 },
        startedAt: '2026-05-12T10:10:01.000Z',
        completedAt: '2026-05-12T10:10:08.000Z',
        inputChannelName: 'Trigger',
        outputChannelNames: ['Success'],
        outgoingConnections: [
          {
            channelName: 'Success',
            blueprintNodeId: 'node-summary',
            anchor: 1,
            side: 'left',
            position: 0.5,
            propertyMappings: [
              { sourceProperty: 'forecast', targetProperty: 'forecast' },
              { sourceProperty: 'riskScore', targetProperty: 'riskScore' },
            ],
          },
        ],
      },
      {
        instanceId: 'node-summary',
        nodeKind: 'PluginTask',
        taskDefinitionId: 102,
        allowedMajor: 1,
        allowedMinor: 0,
        resolvedVersionId: 2001,
        resolvedVersion: '1.0.0',
        name: 'Dispatch Summary',
        status: processId === initialProcesses[1]!.processId ? 'Failed' : 'Completed',
        position: { x: 520, y: 120 },
        startedAt: '2026-05-12T10:10:09.000Z',
        completedAt: processId === initialProcesses[1]!.processId ? null : '2026-05-12T10:10:14.000Z',
        failedAt: processId === initialProcesses[1]!.processId ? '2026-05-12T10:10:14.000Z' : null,
        failureMessage: processId === initialProcesses[1]!.processId ? 'Dispatch channel unavailable.' : null,
        inputChannelName: 'Input',
        outputChannelNames: ['Done'],
        outgoingConnections: [],
      },
    ],
  };
}

export function stepDetail(nodeId: string): ProcessStepDetailViewModel {
  const isSummary = nodeId === 'node-summary';
  return {
    blueprintNodeId: nodeId,
    name: isSummary ? 'Dispatch Summary' : 'Weather Lookup',
    nodeKind: 'PluginTask',
    status: 'Completed',
    resolvedVersion: '1.0.0',
    startedAt: '2026-05-12T10:10:01.000Z',
    endedAt: '2026-05-12T10:10:14.000Z',
    durationMs: isSummary ? 5000 : 7000,
    inputs: isSummary
      ? { forecast: 'Heavy rain expected', riskScore: 0.82 }
      : { city: 'London', severity: 'High' },
    outputs: isSummary
      ? { summary: 'High disruption risk for London.' }
      : { forecast: 'Heavy rain expected', riskScore: 0.82 },
    incomingMappings: isSummary
      ? [{
          sourceBlueprintNodeId: 'node-weather',
          sourceName: 'Weather Lookup',
          channelName: 'Success',
          propertyMappings: [
            { sourceProperty: 'forecast', targetProperty: 'forecast' },
            { sourceProperty: 'riskScore', targetProperty: 'riskScore' },
          ],
        }]
      : [],
    designTimeInputOverrides: {},
    runtimeOverrides: {},
  };
}

export const initialPhasedBlueprints: PhasedBlueprintViewModel[] = [
  {
    id: 50,
    key: 'airport-readiness-series',
    name: 'Airport Readiness Series',
    description: 'Two-phase readiness handoff.',
    versions: [
      {
        phasedBlueprintDefinitionId: 50,
        phasedBlueprintVersionId: 500,
        version: 1,
        description: null,
        createdAt: now,
        phases: [
          {
            phaseId: 'phase-source',
            phaseIndex: 0,
            name: 'Disruption scan',
            blueprintDefinitionId: 10,
            blueprintName: 'Airport Disruption Advisor',
            blueprintVersionId: 100,
            blueprintVersionNumber: 2,
            versionPolicy: 'Pinned',
          },
          {
            phaseId: 'phase-destination',
            phaseIndex: 1,
            name: 'Readiness summary',
            blueprintDefinitionId: 11,
            blueprintName: 'Municipal Readiness Advisor',
            blueprintVersionId: 110,
            blueprintVersionNumber: 1,
            versionPolicy: 'Pinned',
          },
        ],
        transitions: [
          {
            transitionId: 'transition-source-destination',
            sourcePhaseId: 'phase-source',
            destinationPhaseId: 'phase-destination',
            handoffMappings: [
              {
                sourceKind: 'Output',
                sourceBlueprintNodeId: 'node-summary',
                sourceChannelName: 'Done',
                sourceProperty: 'summary',
                bridgePosition: 0.42,
                targetBlueprintNodeId: 'readiness-summary',
                targetProperty: 'forecast',
              },
            ],
            correlationMappings: [],
          },
        ],
      },
    ],
  },
];

export const processSummary: ProcessObservabilitySummaryViewModel = {
  window: '1h',
  from: '2026-05-12T09:30:00.000Z',
  to: now,
  successfulCount: 8,
  failedCount: 2,
  totalCount: 10,
};

export const nodeSummary: NodeObservabilitySummaryViewModel = {
  window: '1h',
  from: '2026-05-12T09:30:00.000Z',
  to: now,
  blueprintDefinitionId: 10,
  blueprintName: 'Airport Disruption Advisor',
  blueprintVersionId: 100,
  blueprintVersionNumber: 2,
  nodes: [
    {
      blueprintNodeId: 'node-weather',
      taskName: 'Weather Lookup',
      taskDefinitionId: 101,
      successfulCount: 10,
      failedCount: 0,
      totalCount: 10,
      successRate: 1,
    },
    {
      blueprintNodeId: 'node-summary',
      taskName: 'Dispatch Summary',
      taskDefinitionId: 102,
      successfulCount: 8,
      failedCount: 2,
      totalCount: 10,
      successRate: 0.8,
    },
  ],
};

export const recentProcesses: RecentProcessesViewModel = {
  window: '1h',
  from: '2026-05-12T09:30:00.000Z',
  to: now,
  processes: initialProcesses.map((process) => ({
    processId: process.processId,
    blueprintDefinitionId: process.blueprintDefinitionId,
    blueprintName: process.blueprintName,
    blueprintVersionId: process.blueprintVersionId,
    blueprintVersionNumber: process.blueprintVersionNumber,
    status: process.status,
    occurredAt: process.updatedAt ?? process.createdAt,
    failureMessage: process.status === 'Failed' ? 'Dispatch channel unavailable.' : null,
  })),
};

export function processActivities(blueprints: BlueprintViewModel[]): BlueprintProcessActivityPageViewModel {
  return {
    items: blueprints.map((blueprint) => ({
      blueprintDefinitionId: blueprint.id,
      blueprintName: blueprint.name,
      recentProcessCount: blueprint.id === 10 ? 2 : 0,
      latestProcessAt: now,
    })),
    page: 1,
    pageSize: 25,
    totalCount: blueprints.length,
    hasMore: false,
    rankingWindowHours: 24,
  };
}

export function historyPage(processes: BlueprintProcessHistoryEntryViewModel[]): BlueprintProcessHistoryPageViewModel {
  return {
    items: processes,
    hasMore: false,
    nextCursor: null,
  };
}

export function searchPage(processes: BlueprintProcessHistoryEntryViewModel[]): ProcessSearchPageViewModel {
  return {
    items: processes,
    hasMore: false,
    nextCursor: null,
  };
}

export function acceptedProcess(processId: string, blueprintId: number): BlueprintProcessAcceptedViewModel {
  return {
    processId,
    blueprintId,
    acceptedAt: now,
  };
}

export const stepLogs: ProcessStepLogBatchViewModel = {
  entries: [
    {
      cursor: 'log-1',
      timestamp: '2026-05-12T10:10:05.000Z',
      level: 'Info',
      serviceName: 'IntegrationSeedPlugin',
      message: 'Loaded weather inputs.',
    },
    {
      cursor: 'log-2',
      timestamp: '2026-05-12T10:10:10.000Z',
      level: 'Info',
      serviceName: 'IntegrationSeedPlugin',
      message: 'Produced operational summary.',
    },
  ],
  nextCursor: null,
};

export const processLogs: ProcessLogBatchViewModel = {
  entries: stepLogs.entries.map((entry) => ({
    ...entry,
    processStepId: 'runtime-step-1',
    blueprintNodeId: 'node-weather',
    taskName: 'Weather Lookup',
    pluginName: 'IntegrationSeedPlugin',
  })),
  nextCursor: null,
};
