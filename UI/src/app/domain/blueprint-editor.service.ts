import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { ConnectionModel } from './connection-model';
import { NodeModel, Point, Side } from './node-model';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';

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
}

interface BlueprintNodeInputModel {
  instanceId: string;
  taskDefinitionId: number;
  allowedMajor: number;
  allowedMinor: number;
  position: CoordinatesViewModel;
  outgoingConnections: BlueprintConnectionInputModel[];
}

interface BlueprintInputModel {
  name: string;
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
}

interface BlueprintNodeViewModel {
  instanceId: string;
  taskDefinitionId: number;
  allowedMajor: number;
  allowedMinor: number;
  resolvedVersionId: number;
  resolvedVersion?: string | null;
  position: CoordinatesViewModel;
  outgoingConnections: BlueprintConnectionViewModel[];
}

export interface BlueprintVersionViewModel {
  blueprintVersionId: number;
  blueprintDefinitionId: number;
  name: string;
  description?: string | null;
  version: number;
  startingBlueprintNodeId: string;
  tasks: BlueprintNodeViewModel[];
}

export interface BlueprintViewModel {
  id: number;
  name: string;
  description: string;
  versions: BlueprintVersionViewModel[];
}

export interface BlueprintProcessInputModel {
  globalState: Record<string, unknown>;
  initialTaskInput: Record<string, unknown>;
}

export interface BlueprintProcessAcceptedViewModel {
  processId: string;
  blueprintId: number;
  acceptedAt: string;
}

export interface ProcessSummaryViewModel {
  processId: string;
  blueprintDefinitionId: number;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  blueprintVersionName: string;
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
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  blueprintVersionName: string;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  createdAt: string;
  updatedAt?: string | null;
}

export interface BlueprintProcessHistoryPageViewModel {
  items: BlueprintProcessHistoryEntryViewModel[];
  nextCursor?: string | null;
  hasMore: boolean;
}

export interface ProcessStepViewModel {
  instanceId: string;
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
  blueprintVersionName: string;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  createdAt: string;
  updatedAt?: string | null;
  startingBlueprintNodeId: string;
  nodes: ProcessStepViewModel[];
}

export interface ProcessStepDetailViewModel {
  blueprintNodeId: string;
  name: string;
  status: 'NotExecuted' | 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  resolvedVersion?: string | null;
  startedAt?: string | null;
  endedAt?: string | null;
  durationMs?: number | null;
  failureMessage?: string | null;
  inputs?: unknown | null;
  outputs?: unknown | null;
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
    hasUpgradeWarning: boolean,
    snap?: (v: number) => number,
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
    );
  }

  getBlueprints(): Observable<BlueprintViewModel[]> {
    return this.http.get<BlueprintViewModel[]>('api/blueprints');
  }

  getBlueprintById(definitionId: number): Observable<BlueprintViewModel> {
    return this.http.get<BlueprintViewModel>(`api/blueprints/${definitionId}`);
  }

  saveBlueprintGraph(
    name: string,
    startingNodeId: number,
    taskDefinitions: ITaskDefinition[],
    nodes: NodeModel[],
    connections: ConnectionModel[],
  ): Observable<BlueprintViewModel> {
    const instanceIdByNodeId = new Map<number, string>(nodes.map((n) => [n.id, crypto.randomUUID()]));

    const tasks: BlueprintNodeInputModel[] = nodes.map((n) => ({
      instanceId: instanceIdByNodeId.get(n.id)!,
      taskDefinitionId: n.taskDefinitionId,
      allowedMajor: n.allowedMajor,
      allowedMinor: n.allowedMinor,
      position: { x: n.x, y: n.y },
      outgoingConnections: [],
    }));

    const taskByNodeId = new Map<number, BlueprintNodeInputModel>(nodes.map((n, index) => [n.id, tasks[index]]));
    const taskDefinitionByVersionId = new Map<number, ITaskDefinition['versions'][number]>();
    for (const taskDefinition of taskDefinitions) {
      for (const version of taskDefinition.versions) {
        taskDefinitionByVersionId.set(version.id, version);
      }
    }

    for (const c of connections) {
      const source = taskByNodeId.get(c.from.id);
      if (!source) continue;

      const sourceTaskVersion = taskDefinitionByVersionId.get(c.from.resolvedVersionId);
      const anchorChannelName = c.from.outputChannelNameForAnchor(c.anchor);
      const defaultOutputChannel = sourceTaskVersion?.outputChannels.find((channel) => channel.name === anchorChannelName)
        ?? sourceTaskVersion?.outputChannels[0];
      source.outgoingConnections.push({
        channelId: null,
        channelName: defaultOutputChannel?.name ?? null,
        connectedBlueprintNodeId: instanceIdByNodeId.get(c.to.id)!,
        anchor: c.anchor,
        side: c.side,
        position: c.pos,
      });
    }

    const payload: BlueprintInputModel = {
      name,
      description: null,
      startingBlueprintNodeId: instanceIdByNodeId.get(startingNodeId)!,
      tasks,
    };

    return this.http.post<BlueprintViewModel>('api/blueprints', payload);
  }

  loadBlueprintGraph(definitionId: number, taskDefinitions: ITaskDefinition[]): Observable<BlueprintGraphDocument> {
    return this.getBlueprintById(definitionId).pipe(
      map((blueprint) => {
        const version = blueprint.versions[0];
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
          const taskDefinition = taskDefinitionById.get(task.taskDefinitionId);
          const taskVersion = taskDefinition
            ? this.resolveVersionForLane(taskDefinition, task.allowedMajor, task.allowedMinor, task.resolvedVersionId)
            : null;
          return this.createNodeModel(
            index + 1,
            task.taskDefinitionId,
            task.allowedMajor,
            task.allowedMinor,
            taskVersion?.id ?? task.resolvedVersionId,
            taskVersion?.version ?? task.resolvedVersion ?? null,
            taskDefinition?.name ?? `Task ${task.taskDefinitionId}`,
            task.position,
            taskVersion?.inputChannel?.name ?? null,
            taskVersion?.outputChannels.map((channel) => channel.name) ?? [],
            taskDefinition ? this.hasNewerMajorMinorLane(taskDefinition, task.allowedMajor, task.allowedMinor) : false,
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
      const [major, minor] = this.getVersionLane(version.version);
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
      const [major, minor] = this.getVersionLane(version.version);
      return major === allowedMajor && minor === allowedMinor;
    });

    if (laneVersions.length === 0) {
      return null;
    }

    return laneVersions[0];
  }

  private getVersionLane(version: `${number}.${number}.${number}` | string): [number, number] {
    const [major, minor] = version.split('.').map((segment) => Number(segment));
    return [Number.isFinite(major) ? major : 0, Number.isFinite(minor) ? minor : 0];
  }
}
