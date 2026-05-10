import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { ConnectionModel, ConnectionPropertyMappingModel } from './connection-model';
import { NodeKind, NodeModel, Point, Side, TaskPropertyBaseType, TaskPropertyType } from './node-model';
import { ITaskChannel, ITaskDefinition } from '../interfaces/ITaskDefinition';
import { taskPropertyKindMap } from './task-property-form-utils';
import { compareTaskVersions, getTaskVersionLane } from './task-version-utils';

interface CoordinatesViewModel {
  x: number;
  y: number;
}

interface BlueprintConnectionInputModel {
  channelId?: number | null;
  channelName?: string | null;
  connectedBlueprintNodeId: string;
  anchor: number;
  side: Side;
  position: number;
  propertyMappings: ConnectionPropertyMappingModel[];
}

interface BlueprintNodeInputModel {
  instanceId: string;
  nodeKind: NodeKind;
  taskDefinitionId?: number | null;
  collectionIteratorItemKind?: TaskPropertyBaseType | null;
  allowedMajor: number;
  allowedMinor: number;
  position: CoordinatesViewModel;
  designTimeInputOverrides: Record<string, unknown>;
  outgoingConnections: BlueprintConnectionInputModel[];
}

interface BlueprintInputModel {
  name: string;
  description?: string | null;
  startingBlueprintNodeId: string;
  tasks: BlueprintNodeInputModel[];
}

interface BlueprintVersionInputModel {
  blueprintDefinitionId: number;
  baseVersion?: number;
  description?: string | null;
  startingBlueprintNodeId: string;
  tasks: BlueprintNodeInputModel[];
}

interface BlueprintConnectionViewModel {
  channelId?: number | null;
  channelName?: string | null;
  blueprintNodeId: string;
  anchor: number;
  side: Side;
  position: number;
  propertyMappings: ConnectionPropertyMappingModel[];
}

interface BlueprintNodeViewModel {
  instanceId: string;
  nodeKind?: NodeKind | null;
  taskDefinitionId?: number | null;
  collectionIteratorItemKind?: TaskPropertyBaseType | null;
  allowedMajor: number;
  allowedMinor: number;
  resolvedVersionId: number;
  resolvedVersion?: string | null;
  position: CoordinatesViewModel;
  designTimeInputOverrides: Record<string, unknown>;
  outgoingConnections: BlueprintConnectionViewModel[];
}

export interface BlueprintVersionViewModel {
  blueprintVersionId: number;
  blueprintDefinitionId: number;
  description?: string | null;
  version: number;
  createdAt?: string | null;
  startingBlueprintNodeId: string;
  tasks: BlueprintNodeViewModel[];
}

export interface BlueprintViewModel {
  id: number;
  name: string;
  description: string;
  versions: BlueprintVersionViewModel[];
}

export type PhasedBlueprintPhaseVersionPolicy = 'Pinned' | 'Latest';
export type PhasedBlueprintHandoffSourceKind = 'Output' | 'GlobalState';

export interface PhasedBlueprintHandoffMappingInputModel {
  sourceKind: PhasedBlueprintHandoffSourceKind;
  sourceBlueprintNodeId?: string | null;
  sourceChannelName?: string | null;
  sourceProperty: string;
  bridgePosition: number;
  targetBlueprintNodeId: string;
  targetProperty: string;
}

export interface PhasedBlueprintCorrelationMappingInputModel {
  sourceResultProperty: string;
  triggerProperty: string;
}

export interface PhasedBlueprintTransitionInputModel {
  transitionId: string;
  sourcePhaseId: string;
  destinationPhaseId: string;
  handoffMappings: PhasedBlueprintHandoffMappingInputModel[];
  correlationMappings: PhasedBlueprintCorrelationMappingInputModel[];
}

export interface PhasedBlueprintPhaseInputModel {
  phaseId: string;
  phaseIndex: number;
  name: string;
  blueprintDefinitionId: number;
  blueprintVersionId?: number | null;
  versionPolicy: PhasedBlueprintPhaseVersionPolicy;
}

export interface PhasedBlueprintInputModel {
  key?: string | null;
  name: string;
  description?: string | null;
  phases: PhasedBlueprintPhaseInputModel[];
  transitions: PhasedBlueprintTransitionInputModel[];
}

export interface PhasedBlueprintVersionInputModel {
  phasedBlueprintDefinitionId: number;
  baseVersion?: number | null;
  description?: string | null;
  phases: PhasedBlueprintPhaseInputModel[];
  transitions: PhasedBlueprintTransitionInputModel[];
}

export interface PhasedBlueprintPhaseViewModel {
  phaseId: string;
  phaseIndex: number;
  name: string;
  blueprintDefinitionId: number;
  blueprintName: string;
  blueprintVersionId?: number | null;
  blueprintVersionNumber?: number | null;
  versionPolicy: PhasedBlueprintPhaseVersionPolicy;
}

export interface PhasedBlueprintTransitionViewModel {
  transitionId: string;
  sourcePhaseId: string;
  destinationPhaseId: string;
  handoffMappings: PhasedBlueprintHandoffMappingInputModel[];
  correlationMappings: PhasedBlueprintCorrelationMappingInputModel[];
}

export interface PhasedBlueprintVersionViewModel {
  phasedBlueprintVersionId: number;
  phasedBlueprintDefinitionId: number;
  version: number;
  description?: string | null;
  createdAt: string;
  phases: PhasedBlueprintPhaseViewModel[];
  transitions: PhasedBlueprintTransitionViewModel[];
}

export interface PhasedBlueprintViewModel {
  id: number;
  key: string;
  name: string;
  description: string;
  versions: PhasedBlueprintVersionViewModel[];
}

export type PhasedProcessStatus = 'Pending' | 'InProgress' | 'WaitingForTrigger' | 'Completed' | 'Failed';

export interface ProcessPhaseMetadataViewModel {
  phasedProcessId: string;
  phasedBlueprintDefinitionId: number;
  phasedBlueprintVersionId: number;
  phaseId: string;
  phaseIndex: number;
  phaseName: string;
}

export interface PhasedProcessPhaseViewModel {
  phaseId: string;
  phaseIndex: number;
  phaseName: string;
  blueprintDefinitionId: number;
  blueprintName: string;
  blueprintVersionId?: number | null;
  childProcessId?: string | null;
  status: PhasedProcessStatus;
  startedAt?: string | null;
  completedAt?: string | null;
  failureMessage?: string | null;
  phaseResult?: unknown | null;
  childProcess?: ProcessDetailViewModel | null;
}

export interface PhasedProcessDetailViewModel {
  phasedProcessId: string;
  phasedBlueprintDefinitionId: number;
  phasedBlueprintName: string;
  phasedBlueprintVersionId: number;
  phasedBlueprintVersionNumber: number;
  status: PhasedProcessStatus;
  createdAt: string;
  updatedAt?: string | null;
  phases: PhasedProcessPhaseViewModel[];
}

export interface BlueprintProcessInputModel {
  globalState: Record<string, unknown>;
  initialTaskInput: Record<string, unknown>;
  nodeOverrides?: Record<string, Record<string, unknown>>;
}

export interface BlueprintProcessAcceptedViewModel {
  processId: string;
  blueprintId: number;
  acceptedAt: string;
}

export interface ProcessSummaryViewModel {
  processId: string;
  blueprintDefinitionId: number;
  blueprintName: string;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  createdAt: string;
  updatedAt?: string | null;
  completedTaskCount: number;
  failedTaskCount: number;
  inProgressTaskCount: number;
  pendingTaskCount: number;
}

export interface BlueprintProcessActivityViewModel {
  blueprintDefinitionId: number;
  blueprintName: string;
  recentProcessCount: number;
  latestProcessAt: string;
}

export interface BlueprintProcessActivityPageViewModel {
  items: BlueprintProcessActivityViewModel[];
  page: number;
  pageSize: number;
  totalCount: number;
  hasMore: boolean;
  rankingWindowHours: number;
}

export interface BlueprintProcessHistoryEntryViewModel {
  processId: string;
  blueprintDefinitionId: number;
  blueprintName: string;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  createdAt: string;
  updatedAt?: string | null;
}

export interface BlueprintProcessHistoryPageViewModel {
  items: BlueprintProcessHistoryEntryViewModel[];
  nextCursor?: string | null;
  hasMore: boolean;
}

export type ProcessSearchStatus = 'Pending' | 'InProgress' | 'Completed' | 'Failed';
export type ProcessSearchPropertyScope = 'GlobalState' | 'NodeInput' | 'NodeOutput';

export interface ProcessSearchBlueprintFilter {
  blueprintDefinitionId: number;
  statuses: ProcessSearchStatus[];
}

export interface ProcessSearchNodeFilter {
  blueprintDefinitionId: number;
  blueprintNodeId: string;
  statuses: ProcessSearchStatus[];
}

export interface ProcessSearchPropertyFilter {
  scope: ProcessSearchPropertyScope;
  blueprintDefinitionId?: number | null;
  blueprintNodeId?: string | null;
  propertyKey: string;
  propertyType: TaskPropertyType;
  value: string;
}

export interface ProcessSearchRequest {
  blueprintDefinitionIds: number[];
  blueprintFilters?: ProcessSearchBlueprintFilter[];
  processStatuses: ProcessSearchStatus[];
  createdFrom: string;
  createdTo: string;
  nodeFilters: ProcessSearchNodeFilter[];
  propertyFilters: ProcessSearchPropertyFilter[];
  limit: number;
  cursor?: string | null;
}

export interface ProcessSearchPageViewModel {
  items: BlueprintProcessHistoryEntryViewModel[];
  nextCursor?: string | null;
  hasMore: boolean;
}

export interface ProcessStepViewModel {
  instanceId: string;
  nodeKind?: NodeKind | null;
  collectionIteratorItemKind?: TaskPropertyBaseType | null;
  runtimeStepCount?: number;
  taskDefinitionId: number;
  allowedMajor: number;
  allowedMinor: number;
  resolvedVersionId: number;
  resolvedVersion?: string | null;
  name: string;
  status: 'NotExecuted' | 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  position: CoordinatesViewModel;
  startedAt?: string | null;
  completedAt?: string | null;
  failedAt?: string | null;
  failureMessage?: string | null;
  inputChannelName?: string | null;
  outputChannelNames: string[];
  outgoingConnections: BlueprintConnectionViewModel[];
}

export interface ProcessDetailViewModel {
  processId: string;
  blueprintDefinitionId: number;
  blueprintName: string;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  createdAt: string;
  updatedAt?: string | null;
  startingBlueprintNodeId: string;
  phase?: ProcessPhaseMetadataViewModel | null;
  nodes: ProcessStepViewModel[];
}

export interface ProcessStepDetailViewModel {
  blueprintNodeId: string;
  name: string;
  nodeKind?: NodeKind | null;
  collectionIteratorItemKind?: TaskPropertyBaseType | null;
  runtimeStepCount?: number;
  status: 'NotExecuted' | 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  resolvedVersion?: string | null;
  startedAt?: string | null;
  endedAt?: string | null;
  durationMs?: number | null;
  failureMessage?: string | null;
  inputs?: unknown | null;
  outputs?: unknown | null;
  incomingMappings: ProcessStepIncomingMappingViewModel[];
  designTimeInputOverrides?: unknown | null;
  runtimeOverrides?: unknown | null;
  iterations?: ProcessStepIterationViewModel[];
}

export interface ProcessStepIterationViewModel {
  runtimeStepId: string;
  sourceIteratorStepId: string;
  sourceIteratorBlueprintNodeId: string;
  itemIndex: number;
  itemKey: string;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
}

export interface ProcessStepIncomingMappingViewModel {
  sourceBlueprintNodeId: string;
  sourceName: string;
  channelName?: string | null;
  propertyMappings: ConnectionPropertyMappingModel[];
}

export interface BlueprintGraphDocument {
  blueprint: BlueprintViewModel;
  nodes: NodeModel[];
  connections: ConnectionModel[];
}

@Injectable({ providedIn: 'root' })
export class BlueprintEditorService {
  private readonly http = inject(HttpClient);

  private createNodeModel(
    id: number,
    taskDefinitionId: number,
    allowedMajor: number,
    allowedMinor: number,
    resolvedVersionId: number,
    resolvedVersion: string | null,
    name: string,
    coordinates: Point,
    inputChannelName: string | null,
    outputChannelNames: string[],
    inputChannelDisplayName: string | null,
    outputChannelDisplayNames: string[],
    hasUpgradeWarning: boolean,
    inputProperties: Record<string, TaskPropertyType>,
    outputChannelProperties: Record<string, Record<string, TaskPropertyType>>,
    outputChannelPropertyDefinitions: Record<string, ITaskChannel['properties']>,
    designTimeInputOverrides: Record<string, unknown>,
    inputPropertyDefinitions: ITaskChannel['properties'],
    snap?: (v: number) => number,
    nodeKind: NodeKind = 'PluginTask',
    collectionIteratorItemKind: TaskPropertyBaseType | null = null,
    instanceId: string | null = null,
  ): NodeModel {
    const y = snap ? snap(coordinates.y) : coordinates.y;
    const outputCount = Math.max(outputChannelNames.length, 1);
    return new NodeModel(
      id,
      taskDefinitionId,
      allowedMajor,
      allowedMinor,
      resolvedVersionId,
      resolvedVersion,
      name,
      snap ? snap(coordinates.x) : coordinates.x,
      y,
      320,
      Math.max(220, 168 + outputCount * 34),
      inputChannelName,
      outputChannelNames,
      hasUpgradeWarning,
      null,
      null,
      inputProperties,
      outputChannelProperties,
      designTimeInputOverrides,
      this.clonePropertyDefinitions(inputPropertyDefinitions),
      inputChannelDisplayName,
      [...outputChannelDisplayNames],
      this.cloneOutputChannelPropertyDefinitions(outputChannelPropertyDefinitions),
      false,
      nodeKind,
      collectionIteratorItemKind,
      instanceId,
    );
  }

  private createCollectionIteratorNodeModel(
    id: number,
    coordinates: Point,
    itemKind: TaskPropertyBaseType | null,
    instanceId: string | null = null,
  ): NodeModel {
    const concreteItemKind = itemKind ?? 'String';
    const collectionKind: TaskPropertyType = `${concreteItemKind}[]`;
    return new NodeModel(
      id,
      0,
      0,
      0,
      0,
      'Native',
      'Collection Iterator',
      coordinates.x,
      coordinates.y,
      NodeModel.collectionIteratorOperatorWidth,
      NodeModel.collectionIteratorOperatorHeight,
      'Collection',
      ['Item'],
      false,
      null,
      null,
      { items: collectionKind },
      { Item: { item: concreteItemKind } },
      {},
      [{
        name: 'items',
        kind: collectionKind,
        required: true,
        enumValues: [],
      }],
      'Collection',
      ['Item'],
      {
        Item: [{
          name: 'item',
          kind: concreteItemKind,
          required: true,
          enumValues: [],
        }],
      },
      false,
      'CollectionIterator',
      itemKind,
      instanceId,
    );
  }

  getBlueprints(): Observable<BlueprintViewModel[]> {
    return this.http.get<BlueprintViewModel[]>('api/blueprints');
  }

  getBlueprintById(definitionId: number): Observable<BlueprintViewModel> {
    return this.http.get<BlueprintViewModel>(`api/blueprints/${definitionId}`);
  }

  createBlueprintGraph(
    name: string,
    startingNodeId: number,
    taskDefinitions: ITaskDefinition[],
    nodes: NodeModel[],
    connections: ConnectionModel[],
  ): Observable<BlueprintViewModel> {
    const graph = this.buildGraphPayload(startingNodeId, taskDefinitions, nodes, connections);
    const payload: BlueprintInputModel = {
      name,
      description: null,
      startingBlueprintNodeId: graph.startingBlueprintNodeId,
      tasks: graph.tasks,
    };

    return this.http.post<BlueprintViewModel>('api/blueprints', payload);
  }

  createBlueprintVersion(
    definitionId: number,
    baseVersion: number | null,
    startingNodeId: number,
    taskDefinitions: ITaskDefinition[],
    nodes: NodeModel[],
    connections: ConnectionModel[],
  ): Observable<BlueprintVersionViewModel> {
    const graph = this.buildGraphPayload(startingNodeId, taskDefinitions, nodes, connections);
    const payload: BlueprintVersionInputModel = {
      blueprintDefinitionId: definitionId,
      ...(baseVersion ? { baseVersion } : {}),
      description: null,
      startingBlueprintNodeId: graph.startingBlueprintNodeId,
      tasks: graph.tasks,
    };

    return this.http.post<BlueprintVersionViewModel>(`api/blueprints/${definitionId}/versions`, payload);
  }

  createPhasedBlueprint(payload: PhasedBlueprintInputModel): Observable<PhasedBlueprintViewModel> {
    return this.http.post<PhasedBlueprintViewModel>('api/phased-blueprints', payload);
  }

  getPhasedBlueprints(): Observable<PhasedBlueprintViewModel[]> {
    return this.http.get<PhasedBlueprintViewModel[]>('api/phased-blueprints');
  }

  getPhasedBlueprintById(definitionId: number): Observable<PhasedBlueprintViewModel> {
    return this.http.get<PhasedBlueprintViewModel>(`api/phased-blueprints/${definitionId}`);
  }

  createPhasedBlueprintVersion(
    definitionId: number,
    payload: PhasedBlueprintVersionInputModel,
  ): Observable<PhasedBlueprintVersionViewModel> {
    return this.http.post<PhasedBlueprintVersionViewModel>(`api/phased-blueprints/${definitionId}/versions`, payload);
  }

  getPhasedProcess(
    phasedBlueprintDefinitionId: number,
    phasedProcessId: string,
  ): Observable<PhasedProcessDetailViewModel> {
    return this.http.get<PhasedProcessDetailViewModel>(
      `api/phased-blueprints/${phasedBlueprintDefinitionId}/processes/${phasedProcessId}`,
    );
  }

  private buildGraphPayload(
    startingNodeId: number,
    taskDefinitions: ITaskDefinition[],
    nodes: NodeModel[],
    connections: ConnectionModel[],
  ): {
    startingBlueprintNodeId: string;
    tasks: BlueprintNodeInputModel[];
  } {
    const persistedNodes = nodes.filter((node) => node.nodeKind !== 'DataBridge');
    const persistedConnections = connections.filter((connection) =>
      connection.from.nodeKind !== 'DataBridge' &&
      connection.to.nodeKind !== 'DataBridge');
    const instanceIdByNodeId = new Map<number, string>(persistedNodes.map((n) => [n.id, n.instanceId ?? crypto.randomUUID()]));

    const tasks: BlueprintNodeInputModel[] = persistedNodes.map((n) => ({
      instanceId: instanceIdByNodeId.get(n.id)!,
      nodeKind: n.nodeKind,
      taskDefinitionId: n.nodeKind === 'PluginTask' ? n.taskDefinitionId : null,
      collectionIteratorItemKind: n.nodeKind === 'CollectionIterator' ? n.collectionIteratorItemKind : null,
      allowedMajor: n.allowedMajor,
      allowedMinor: n.allowedMinor,
      position: { x: n.x, y: n.y },
      designTimeInputOverrides: { ...n.designTimeInputOverrides },
      outgoingConnections: [],
    }));

    const taskByNodeId = new Map<number, BlueprintNodeInputModel>(persistedNodes.map((n, index) => [n.id, tasks[index]]));
    const taskDefinitionByVersionId = new Map<number, ITaskDefinition['versions'][number]>();
    for (const taskDefinition of taskDefinitions) {
      for (const version of taskDefinition.versions) {
        taskDefinitionByVersionId.set(version.id, version);
      }
    }

    for (const c of persistedConnections) {
      const source = taskByNodeId.get(c.from.id);
      if (!source) continue;

      const sourceTaskVersion = taskDefinitionByVersionId.get(c.from.resolvedVersionId);
      const anchorChannelName = c.from.outputChannelNameForAnchor(c.anchor);
      const defaultOutputChannel = sourceTaskVersion?.outputChannels.find((channel) => channel.name === anchorChannelName)
        ?? sourceTaskVersion?.outputChannels[0];
      source.outgoingConnections.push({
        channelId: null,
        channelName: anchorChannelName ?? defaultOutputChannel?.name ?? null,
        connectedBlueprintNodeId: instanceIdByNodeId.get(c.to.id)!,
        anchor: c.anchor,
        side: c.side,
        position: c.pos,
        propertyMappings: c.propertyMappings.map((mapping) => ({ ...mapping })),
      });
    }

    return {
      startingBlueprintNodeId: instanceIdByNodeId.get(startingNodeId) ?? instanceIdByNodeId.get(persistedNodes[0]?.id ?? 0)!,
      tasks,
    };
  }

  loadBlueprintGraph(
    definitionId: number,
    taskDefinitions: ITaskDefinition[],
    blueprintVersionId?: number | null,
  ): Observable<BlueprintGraphDocument> {
    return this.getBlueprintById(definitionId).pipe(
      map((blueprint) => {
        const version = blueprintVersionId === undefined || blueprintVersionId === null
          ? blueprint.versions[0]
          : blueprint.versions.find((candidate) => candidate.blueprintVersionId === blueprintVersionId) ?? blueprint.versions[0];
        if (!version) {
          return {
            blueprint,
            nodes: [],
            connections: [],
          };
        }

        const taskDefinitionById = new Map<number, ITaskDefinition>();
        for (const taskDefinition of taskDefinitions) {
          taskDefinitionById.set(taskDefinition.id, taskDefinition);
        }

        const nodes = version.tasks.map((task, index) => {
          if (task.nodeKind === 'CollectionIterator') {
            return this.createCollectionIteratorNodeModel(
              index + 1,
              task.position,
              this.normalizeCollectionIteratorItemKind(task.collectionIteratorItemKind),
              task.instanceId,
            );
          }

          const taskDefinitionId = task.taskDefinitionId ?? 0;
          const taskDefinition = taskDefinitionById.get(taskDefinitionId);
          const taskVersion = taskDefinition
            ? this.resolveVersionForLane(taskDefinition, task.allowedMajor, task.allowedMinor, task.resolvedVersionId)
            : null;
          return this.createNodeModel(
            index + 1,
            taskDefinitionId,
            task.allowedMajor,
            task.allowedMinor,
            taskVersion?.id ?? task.resolvedVersionId,
            taskVersion?.version ?? task.resolvedVersion ?? null,
            taskDefinition ? this.taskDefinitionDisplayName(taskDefinition) : `Task ${taskDefinitionId}`,
            task.position,
            taskVersion?.inputChannel?.name ?? null,
            taskVersion?.outputChannels.map((channel) => channel.name) ?? [],
            taskVersion?.inputChannel?.displayName ?? null,
            (taskVersion?.outputChannels ?? []).map((channel) => channel.displayName?.trim() || channel.name),
            taskDefinition ? this.hasNewerMajorMinorLane(taskDefinition, task.allowedMajor, task.allowedMinor) : false,
            taskPropertyKindMap(taskVersion?.inputChannel?.properties ?? []),
            Object.fromEntries((taskVersion?.outputChannels ?? []).map((channel) => [channel.name, this.channelPropertyKindMap(channel)])),
            Object.fromEntries((taskVersion?.outputChannels ?? []).map((channel) => [channel.name, this.clonePropertyDefinitions(channel.properties)])),
            task.designTimeInputOverrides ?? {},
            taskVersion?.inputChannel?.properties ?? [],
            undefined,
            'PluginTask',
            null,
            task.instanceId,
          );
        });

        const nodeIdByInstanceId = new Map<string, number>(version.tasks.map((task, index) => [task.instanceId, index + 1]));
        const nodeById = new Map<number, NodeModel>(nodes.map((node) => [node.id, node]));

        const connections = version.tasks.flatMap((task) =>
          task.outgoingConnections.flatMap((connection) => {
            const fromNodeId = nodeIdByInstanceId.get(task.instanceId);
            const toNodeId = nodeIdByInstanceId.get(connection.blueprintNodeId);
            if (!fromNodeId || !toNodeId) {
              return [];
            }

            return [new ConnectionModel(
              nodeById.get(fromNodeId)!,
              nodeById.get(toNodeId)!,
              connection.anchor,
              connection.side,
              connection.position,
              crypto.randomUUID(),
              connection.propertyMappings.map((mapping) => ({ ...mapping })),
            )];
          }),
        );

        return {
          blueprint,
          nodes,
          connections,
        };
      }),
    );
  }

  startProcess(definitionId: number, payload: BlueprintProcessInputModel): Observable<BlueprintProcessAcceptedViewModel> {
    return this.http.post<BlueprintProcessAcceptedViewModel>(`api/blueprints/${definitionId}/processes`, payload);
  }

  getBlueprintProcesses(definitionId: number): Observable<ProcessSummaryViewModel[]> {
    return this.http.get<ProcessSummaryViewModel[]>(`api/blueprints/${definitionId}/processes`);
  }

  getProcessBlueprintActivities(
    query: string,
    page: number,
    pageSize: number,
  ): Observable<BlueprintProcessActivityPageViewModel> {
    return this.http.get<BlueprintProcessActivityPageViewModel>('api/processes/blueprints', {
      params: {
        query,
        page,
        pageSize,
      },
    });
  }

  getBlueprintProcessHistory(
    definitionId: number,
    limit: number,
    cursor?: string | null,
  ): Observable<BlueprintProcessHistoryPageViewModel> {
    return this.http.get<BlueprintProcessHistoryPageViewModel>(`api/blueprints/${definitionId}/processes/history`, {
      params: {
        limit,
        ...(cursor ? { cursor } : {}),
      },
    });
  }

  searchProcesses(request: ProcessSearchRequest): Observable<ProcessSearchPageViewModel> {
    return this.http.post<ProcessSearchPageViewModel>('api/processes/search', request);
  }

  getBlueprintProcess(definitionId: number, processId: string): Observable<ProcessDetailViewModel> {
    return this.http.get<ProcessDetailViewModel>(`api/blueprints/${definitionId}/processes/${processId}`);
  }

  getProcessStepDetail(
    definitionId: number,
    processId: string,
    blueprintNodeId: string,
  ): Observable<ProcessStepDetailViewModel> {
    return this.http.get<ProcessStepDetailViewModel>(
      `api/blueprints/${definitionId}/processes/${processId}/steps/${blueprintNodeId}`,
    );
  }

  hasNewerMajorMinorLane(taskDefinition: ITaskDefinition, allowedMajor: number, allowedMinor: number): boolean {
    return taskDefinition.versions.some((version) => {
      const [major, minor] = getTaskVersionLane(version.version);
      return major > allowedMajor || (major === allowedMajor && minor > allowedMinor);
    });
  }

  resolveVersionForLane(
    taskDefinition: ITaskDefinition,
    allowedMajor: number,
    allowedMinor: number,
    preferredVersionId?: number | null,
  ): ITaskDefinition['versions'][number] | null {
    if (preferredVersionId) {
      const preferredVersion = taskDefinition.versions.find((version) => version.id === preferredVersionId);
      if (preferredVersion) {
        return preferredVersion;
      }
    }

    const laneVersions = taskDefinition.versions.filter((version) => {
      const [major, minor] = getTaskVersionLane(version.version);
      return major === allowedMajor && minor === allowedMinor;
    });

    if (laneVersions.length === 0) {
      return null;
    }

    return [...laneVersions].sort((left, right) => compareTaskVersions(right, left))[0];
  }

  private channelPropertyKindMap(channel: ITaskChannel): Record<string, TaskPropertyType> {
    return taskPropertyKindMap(channel.properties);
  }

  private taskDefinitionDisplayName(taskDefinition: ITaskDefinition): string {
    const displayName = taskDefinition.displayName?.trim();
    return displayName && displayName.length > 0
      ? displayName
      : taskDefinition.name;
  }

  private normalizeCollectionIteratorItemKind(kind: TaskPropertyBaseType | null | undefined): TaskPropertyBaseType | null {
    const supported: readonly TaskPropertyBaseType[] = [
      'String',
      'Integer',
      'Decimal',
      'Boolean',
      'Guid',
      'Date',
      'DateTime',
      'Enum',
    ];

    return kind && supported.includes(kind) ? kind : null;
  }

  private clonePropertyDefinitions(properties: ITaskChannel['properties']): ITaskChannel['properties'] {
    return properties.map((property) => ({
      ...property,
      enumValues: [...property.enumValues],
    }));
  }

  private cloneOutputChannelPropertyDefinitions(
    outputChannelProperties: Record<string, ITaskChannel['properties']>,
  ): Record<string, ITaskChannel['properties']> {
    return Object.fromEntries(
      Object.entries(outputChannelProperties).map(([channelName, properties]) => [
        channelName,
        this.clonePropertyDefinitions(properties),
      ]),
    );
  }
}
