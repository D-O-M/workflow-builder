import type { Page, Route } from '@playwright/test';
import type {
  BlueprintProcessHistoryEntryViewModel,
  BlueprintVersionViewModel,
  BlueprintViewModel,
  PhasedBlueprintInputModel,
  PhasedBlueprintVersionInputModel,
  PhasedBlueprintVersionViewModel,
  PhasedBlueprintViewModel,
} from '../../src/app/domain/blueprint-editor.service';
import {
  acceptedProcess,
  historyPage,
  initialBlueprints,
  initialPhasedBlueprints,
  initialProcesses,
  nodeSummary,
  processActivities,
  processDetail,
  processLogs,
  processSummary,
  recentProcesses,
  searchPage,
  stepDetail,
  stepLogs,
  taskDefinitions,
} from '../fixtures/workflow-fixtures';

import type { ConditionalRule } from '../../src/app/domain/conditional-rule';
import type { NodeKind } from '../../src/app/domain/node-model';
import type { ITaskPropertyDefinition } from '../../src/app/interfaces/ITaskDefinition';

type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

interface MockWorkflowApiOptions {
  readonly failDashboard?: boolean;
  readonly failBlueprintSave?: boolean;
  readonly failStartProcess?: boolean;
}

interface BlueprintNodeInputModel {
  readonly instanceId: string;
  readonly nodeKind?: NodeKind | null;
  readonly conditionalRule?: ConditionalRule | null;
  readonly conditionalFields?: ITaskPropertyDefinition[] | null;
  readonly taskDefinitionId?: number | null;
  readonly collectionIteratorItemKind?: string | null;
  readonly allowedMajor: number;
  readonly allowedMinor: number;
  readonly position: { readonly x: number; readonly y: number };
  readonly designTimeInputOverrides?: Record<string, unknown>;
  readonly outgoingConnections: readonly {
    readonly channelName?: string | null;
    readonly connectedBlueprintNodeId: string;
    readonly anchor: number;
    readonly side: 'left' | 'right' | 'top' | 'bottom';
    readonly position: number;
    readonly propertyMappings: readonly { readonly sourceProperty: string; readonly targetProperty: string }[];
  }[];
}

interface BlueprintInputModel {
  readonly name: string;
  readonly description?: string | null;
  readonly startingBlueprintNodeId: string;
  readonly tasks: readonly BlueprintNodeInputModel[];
}

interface BlueprintVersionInputModel extends BlueprintInputModel {
  readonly blueprintDefinitionId: number;
  readonly baseVersion?: number | null;
}

export class MockWorkflowApi {
  private readonly options: MockWorkflowApiOptions;
  private blueprints: BlueprintViewModel[];
  private phasedBlueprints: PhasedBlueprintViewModel[];
  private processes: BlueprintProcessHistoryEntryViewModel[];
  private nextBlueprintId = 100;
  private nextBlueprintVersionId = 1000;
  private nextPhasedBlueprintId = 200;
  private nextPhasedBlueprintVersionId = 2000;
  private nextProcessIndex = 3;

  constructor(options: MockWorkflowApiOptions = {}) {
    this.options = options;
    this.blueprints = structuredClone(initialBlueprints);
    this.phasedBlueprints = structuredClone(initialPhasedBlueprints);
    this.processes = structuredClone(initialProcesses);
  }

  async install(page: Page): Promise<void> {
    await page.route('**/api/client-config', (route) => this.fulfillJson(route, {
      apiBaseUrl: 'http://127.0.0.1:4200',
      signalRBaseUrl: 'http://127.0.0.1:4200',
      grafanaBaseUrl: 'http://127.0.0.1:3000',
      features: { conditionalBranchAuthoring: true },
    }));
    await page.route('**/tasks', (route) => this.fulfillJson(route, taskDefinitions));
    await page.route('**/hubs/processes/negotiate**', (route) => this.fulfillJson(route, {
      connectionId: 'mocked-signalr',
      connectionToken: 'mocked-signalr-token',
      negotiateVersion: 1,
      availableTransports: [],
    }));
    await page.route('**/api/**', (route) => this.dispatch(route));
  }

  lastCreatedBlueprint(): BlueprintViewModel | null {
    return this.blueprints.at(-1) ?? null;
  }

  addBlueprints(blueprints: readonly BlueprintViewModel[]): void {
    this.blueprints.push(...structuredClone(blueprints));
  }

  private async dispatch(route: Route): Promise<void> {
    const request = route.request();
    const method = request.method().toUpperCase() as HttpMethod;
    const url = new URL(request.url());
    const path = url.pathname;

    try {
      if (method === 'GET' && path === '/api/client-config') {
        await this.fulfillJson(route, {
          apiBaseUrl: 'http://127.0.0.1:4200',
          signalRBaseUrl: 'http://127.0.0.1:4200',
          grafanaBaseUrl: 'http://127.0.0.1:3000',
          features: { conditionalBranchAuthoring: true },
        });
        return;
      }

      if (method === 'GET' && path === '/api/blueprints') {
        await this.fulfillJson(route, this.blueprints);
        return;
      }

      if (method === 'GET' && /^\/api\/blueprints\/\d+$/.test(path)) {
        const id = Number(path.split('/').at(-1));
        await this.fulfillJson(route, this.requireBlueprint(id));
        return;
      }

      if (method === 'POST' && path === '/api/blueprints') {
        if (this.options.failBlueprintSave) {
          await this.fulfillJson(route, { message: 'Save failed' }, 500);
          return;
        }

        const payload = await request.postDataJSON() as BlueprintInputModel;
        const created = this.createBlueprint(payload);
        await this.fulfillJson(route, created);
        return;
      }

      const blueprintVersionMatch = path.match(/^\/api\/blueprints\/(\d+)\/versions$/);
      if (method === 'POST' && blueprintVersionMatch) {
        const id = Number(blueprintVersionMatch[1]);
        const payload = await request.postDataJSON() as BlueprintVersionInputModel;
        const version = this.createBlueprintVersion(id, payload);
        await this.fulfillJson(route, version);
        return;
      }

      const startProcessMatch = path.match(/^\/api\/blueprints\/(\d+)\/processes$/);
      if (method === 'POST' && startProcessMatch) {
        if (this.options.failStartProcess) {
          await this.fulfillJson(route, { message: 'Process launch failed' }, 500);
          return;
        }

        const blueprintId = Number(startProcessMatch[1]);
        const processId = `33333333-3333-4333-8333-${String(this.nextProcessIndex++).padStart(12, '3')}`;
        this.processes.unshift({
          processId,
          blueprintDefinitionId: blueprintId,
          blueprintName: this.requireBlueprint(blueprintId).name,
          blueprintVersionId: this.requireBlueprint(blueprintId).versions[0]?.blueprintVersionId ?? 0,
          blueprintVersionNumber: this.requireBlueprint(blueprintId).versions[0]?.version ?? 1,
          status: 'Completed',
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
        });
        await this.fulfillJson(route, acceptedProcess(processId, blueprintId));
        return;
      }

      if (method === 'GET' && path === '/api/processes/blueprints') {
        await this.fulfillJson(route, processActivities(this.blueprints));
        return;
      }

      const historyMatch = path.match(/^\/api\/blueprints\/(\d+)\/processes\/history$/);
      if (method === 'GET' && historyMatch) {
        const blueprintId = Number(historyMatch[1]);
        await this.fulfillJson(route, historyPage(this.processes.filter((process) => process.blueprintDefinitionId === blueprintId)));
        return;
      }

      if (method === 'POST' && path === '/api/processes/search') {
        await this.fulfillJson(route, searchPage(this.processes));
        return;
      }

      const processMatch = path.match(/^\/api\/blueprints\/(\d+)\/processes\/([^/]+)$/);
      if (method === 'GET' && processMatch) {
        await this.fulfillJson(route, processDetail(processMatch[2]));
        return;
      }

      const stepMatch = path.match(/^\/api\/blueprints\/(\d+)\/processes\/([^/]+)\/steps\/([^/]+)$/);
      if (method === 'GET' && stepMatch) {
        await this.fulfillJson(route, stepDetail(stepMatch[3]));
        return;
      }

      if (method === 'GET' && /\/logs$/.test(path) && path.includes('/steps/')) {
        await this.fulfillJson(route, stepLogs);
        return;
      }

      if (method === 'GET' && /^\/api\/observability\/processes\/[^/]+\/logs$/.test(path)) {
        await this.fulfillJson(route, processLogs);
        return;
      }

      if (method === 'GET' && path === '/api/observability/processes/summary') {
        await this.fulfillJson(route, this.options.failDashboard ? { message: 'Dashboard unavailable' } : processSummary, this.options.failDashboard ? 500 : 200);
        return;
      }

      if (method === 'GET' && path === '/api/observability/nodes/summary') {
        await this.fulfillJson(route, nodeSummary);
        return;
      }

      if (method === 'GET' && path === '/api/observability/processes/recent') {
        await this.fulfillJson(route, recentProcesses);
        return;
      }

      if (method === 'GET' && path === '/api/phased-blueprints') {
        await this.fulfillJson(route, this.phasedBlueprints);
        return;
      }

      if (method === 'GET' && /^\/api\/phased-blueprints\/\d+$/.test(path)) {
        const id = Number(path.split('/').at(-1));
        await this.fulfillJson(route, this.requirePhasedBlueprint(id));
        return;
      }

      if (method === 'POST' && path === '/api/phased-blueprints') {
        const payload = await request.postDataJSON() as PhasedBlueprintInputModel;
        const created = this.createPhasedBlueprint(payload);
        await this.fulfillJson(route, created);
        return;
      }

      const phasedVersionMatch = path.match(/^\/api\/phased-blueprints\/(\d+)\/versions$/);
      if (method === 'POST' && phasedVersionMatch) {
        const id = Number(phasedVersionMatch[1]);
        const payload = await request.postDataJSON() as PhasedBlueprintVersionInputModel;
        const version = this.createPhasedBlueprintVersion(id, payload);
        await this.fulfillJson(route, version);
        return;
      }

      await this.fulfillJson(route, { message: `No mock route for ${method} ${path}` }, 404);
    } catch (error) {
      await this.fulfillJson(route, {
        message: error instanceof Error ? error.message : 'Mock route failed',
      }, 500);
    }
  }

  private createBlueprint(payload: BlueprintInputModel): BlueprintViewModel {
    const id = this.nextBlueprintId++;
    const version = this.createVersionFromPayload(id, 1, payload);
    const blueprint: BlueprintViewModel = {
      id,
      name: payload.name,
      description: payload.description ?? '',
      versions: [version],
    };
    this.blueprints.push(blueprint);
    return blueprint;
  }

  private createBlueprintVersion(definitionId: number, payload: BlueprintVersionInputModel): BlueprintVersionViewModel {
    const blueprint = this.requireBlueprint(definitionId);
    const nextVersionNumber = Math.max(...blueprint.versions.map((version) => version.version), 0) + 1;
    const version = this.createVersionFromPayload(definitionId, nextVersionNumber, payload);
    blueprint.versions.unshift(version);
    return version;
  }

  private createVersionFromPayload(
    blueprintDefinitionId: number,
    versionNumber: number,
    payload: BlueprintInputModel,
  ): BlueprintVersionViewModel {
    return {
      blueprintDefinitionId,
      blueprintVersionId: this.nextBlueprintVersionId++,
      version: versionNumber,
      description: payload.description ?? null,
      createdAt: new Date().toISOString(),
      startingBlueprintNodeId: payload.startingBlueprintNodeId,
      tasks: payload.tasks.map((task) => {
        const definition = taskDefinitions.find((candidate) => candidate.id === task.taskDefinitionId);
        const version = definition?.versions[0];
        return {
          instanceId: task.instanceId,
          nodeKind: task.nodeKind ?? 'PluginTask',
          conditionalRule: task.conditionalRule,
          conditionalFields: task.conditionalFields,
          taskDefinitionId: task.taskDefinitionId ?? null,
          collectionIteratorItemKind: task.collectionIteratorItemKind ?? null,
          allowedMajor: task.allowedMajor,
          allowedMinor: task.allowedMinor,
          resolvedVersionId: version?.id ?? 0,
          resolvedVersion: version?.version ?? null,
          position: { ...task.position },
          designTimeInputOverrides: { ...(task.designTimeInputOverrides ?? {}) },
          outgoingConnections: task.outgoingConnections.map((connection) => ({
            channelName: connection.channelName ?? null,
            blueprintNodeId: connection.connectedBlueprintNodeId,
            anchor: connection.anchor,
            side: connection.side,
            position: connection.position,
            propertyMappings: connection.propertyMappings.map((mapping) => ({ ...mapping })),
          })),
        };
      }),
    };
  }

  private createPhasedBlueprint(payload: PhasedBlueprintInputModel): PhasedBlueprintViewModel {
    const id = this.nextPhasedBlueprintId++;
    const version = this.createPhasedVersion(id, 1, payload);
    const series: PhasedBlueprintViewModel = {
      id,
      key: payload.key ?? `phase-series-${id}`,
      name: payload.name,
      description: payload.description ?? '',
      versions: [version],
    };
    this.phasedBlueprints.push(series);
    return series;
  }

  private createPhasedBlueprintVersion(
    definitionId: number,
    payload: PhasedBlueprintVersionInputModel,
  ): PhasedBlueprintVersionViewModel {
    const series = this.requirePhasedBlueprint(definitionId);
    const nextVersionNumber = Math.max(...series.versions.map((version) => version.version), 0) + 1;
    const version = this.createPhasedVersion(definitionId, nextVersionNumber, payload);
    series.versions.unshift(version);
    return version;
  }

  private createPhasedVersion(
    phasedBlueprintDefinitionId: number,
    versionNumber: number,
    payload: PhasedBlueprintInputModel | PhasedBlueprintVersionInputModel,
  ): PhasedBlueprintVersionViewModel {
    return {
      phasedBlueprintDefinitionId,
      phasedBlueprintVersionId: this.nextPhasedBlueprintVersionId++,
      version: versionNumber,
      description: payload.description ?? null,
      createdAt: new Date().toISOString(),
      phases: payload.phases.map((phase) => {
        const blueprint = this.requireBlueprint(phase.blueprintDefinitionId);
        const version = phase.blueprintVersionId
          ? blueprint.versions.find((candidate) => candidate.blueprintVersionId === phase.blueprintVersionId) ?? blueprint.versions[0]
          : blueprint.versions[0];
        return {
          phaseId: phase.phaseId,
          phaseIndex: phase.phaseIndex,
          name: phase.name,
          blueprintDefinitionId: phase.blueprintDefinitionId,
          blueprintName: blueprint.name,
          blueprintVersionId: phase.blueprintVersionId ?? version?.blueprintVersionId ?? null,
          blueprintVersionNumber: version?.version ?? null,
          versionPolicy: phase.versionPolicy,
        };
      }),
      transitions: payload.transitions.map((transition) => ({
        transitionId: transition.transitionId,
        sourcePhaseId: transition.sourcePhaseId,
        destinationPhaseId: transition.destinationPhaseId,
        handoffMappings: transition.handoffMappings.map((mapping) => ({ ...mapping })),
        correlationMappings: transition.correlationMappings.map((mapping) => ({ ...mapping })),
      })),
    };
  }

  private requireBlueprint(id: number): BlueprintViewModel {
    const blueprint = this.blueprints.find((candidate) => candidate.id === id);
    if (!blueprint) {
      throw new Error(`Blueprint ${id} was not found.`);
    }
    return blueprint;
  }

  private requirePhasedBlueprint(id: number): PhasedBlueprintViewModel {
    const series = this.phasedBlueprints.find((candidate) => candidate.id === id);
    if (!series) {
      throw new Error(`Phased blueprint ${id} was not found.`);
    }
    return series;
  }

  private async fulfillJson(route: Route, body: unknown, status = 200): Promise<void> {
    await route.fulfill({
      status,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
  }
}
