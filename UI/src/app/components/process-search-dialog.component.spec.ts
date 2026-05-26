import '@angular/compiler';
import {
  EnvironmentInjector,
  Provider,
  createEnvironmentInjector,
  runInInjectionContext,
} from '@angular/core';
import { ɵChangeDetectionScheduler as ChangeDetectionScheduler, ɵEffectScheduler as EffectScheduler } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ProcessSearchDialogComponent } from './process-search-dialog.component';
import {
  BlueprintEditorService,
  BlueprintViewModel,
  ProcessSearchRequest,
} from '../domain/blueprint-editor.service';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';

function createTaskDefinitions(): ITaskDefinition[] {
  return [
    {
      id: 11,
      name: 'Webhook Listener',
      versions: [
        {
          id: 111,
          version: '1.0.0',
          state: 'Active',
          description: 'Trigger',
          inputChannel: null,
          outputChannels: [
            {
              name: 'Success',
              properties: {
                user_id: 'String',
              },
            },
          ],
        },
      ],
    },
    {
      id: 12,
      name: 'POST /v1/auth',
      versions: [
        {
          id: 121,
          version: '1.0.0',
          state: 'Active',
          description: 'HTTP',
          inputChannel: {
            name: 'Input',
            properties: {
              user_id: 'String',
            },
          },
          outputChannels: [
            {
              name: 'Success',
              properties: {
                token: 'String',
              },
            },
          ],
        },
      ],
    },
  ];
}

function createBlueprints(): BlueprintViewModel[] {
  return [
    {
      id: 1,
      name: 'InventorySync_Main',
      description: '',
      versions: [
        {
          blueprintDefinitionId: 1,
          blueprintVersionId: 101,
          description: null,
          version: 3,
          startingBlueprintNodeId: 'node-a',
          tasks: [
            {
              instanceId: 'node-a',
              taskDefinitionId: 11,
              allowedMajor: 1,
              allowedMinor: 0,
              resolvedVersionId: 111,
              resolvedVersion: '1.0.0',
              position: { x: 0, y: 0 },
              designTimeInputOverrides: {},
              outgoingConnections: [],
            },
          ],
        },
      ],
    },
    {
      id: 2,
      name: 'DataMigration_v2',
      description: '',
      versions: [
        {
          blueprintDefinitionId: 2,
          blueprintVersionId: 201,
          description: null,
          version: 4,
          startingBlueprintNodeId: 'node-b',
          tasks: [
            {
              instanceId: 'node-b',
              taskDefinitionId: 12,
              allowedMajor: 1,
              allowedMinor: 0,
              resolvedVersionId: 121,
              resolvedVersion: '1.0.0',
              position: { x: 0, y: 0 },
              designTimeInputOverrides: {},
              outgoingConnections: [],
            },
          ],
        },
      ],
    },
  ];
}

describe('ProcessSearchDialogComponent', () => {
  let component: ProcessSearchDialogComponent;
  let injector: EnvironmentInjector;
  let dialogRef: { close: ReturnType<typeof vi.fn> };
  let data: {
    blueprints: BlueprintViewModel[];
    taskDefinitions: ITaskDefinition[];
    initialRequest: ProcessSearchRequest | null;
  };

  beforeEach(() => {
    dialogRef = {
      close: vi.fn(),
    };
    data = {
      blueprints: createBlueprints(),
      taskDefinitions: createTaskDefinitions(),
      initialRequest: null,
    };

    const providers: Provider[] = [
      {
        provide: BlueprintEditorService,
        useValue: {
          resolveVersionForLane: (taskDefinition: ITaskDefinition, _major: number, _minor: number, preferredVersionId?: number | null) =>
            taskDefinition.versions.find((version) => version.id === preferredVersionId) ?? taskDefinition.versions[0] ?? null,
        },
      },
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
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
    component = runInInjectionContext(injector, () => new ProcessSearchDialogComponent());
  });

  afterEach(() => {
    injector.destroy();
  });

  async function flushSignals(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
  }

  it('defaults the execution window to the last fifteen minutes', () => {
    const createdFrom = new Date((component as any).createdFromInput());
    const createdTo = new Date((component as any).createdToInput());
    const deltaMinutes = (createdTo.getTime() - createdFrom.getTime()) / 60_000;

    expect((component as any).quickRange()).toBe('15m');
    expect(deltaMinutes).toBeGreaterThan(14);
    expect(deltaMinutes).toBeLessThan(16);
  });

  it('narrows available node options to the selected blueprints', async () => {
    expect((component as any).filteredNodeOptions().map((node: any) => node.blueprintDefinitionId).sort()).toEqual([1, 2]);

    (component as any).selectedBlueprintIds.set([1]);
    await flushSignals();

    expect((component as any).filteredNodeOptions().map((node: any) => node.blueprintDefinitionId)).toEqual([1]);
  });

  it('prunes invalid property rows when the blueprint scope changes', async () => {
    (component as any).selectedBlueprintIds.set([2]);
    await flushSignals();

    const outputOption = (component as any).propertyCatalog().find((option: any) => option.propertyKey === 'token');
    (component as any).propertyFilterRows.set([
      {
        id: 'row-1',
        optionKey: outputOption.key,
        value: 'abc-123',
      },
    ]);
    await flushSignals();

    expect((component as any).propertyFilterRows().length).toBe(1);

    (component as any).selectedBlueprintIds.set([1, 2]);
    (component as any).removeBlueprint(2);
    await flushSignals();

    expect((component as any).propertyFilterRows()).toEqual([]);
  });

  it('serializes the selected filters into a search request', () => {
    (component as any).selectedBlueprintIds.set([2]);
    (component as any).selectedNodeFilters.set([
      {
        key: '2:node-b',
        blueprintDefinitionId: 2,
        blueprintNodeId: 'node-b',
        statuses: ['Completed'],
      },
    ]);
    const outputOption = (component as any).propertyCatalog().find((option: any) => option.propertyKey === 'token');
    (component as any).propertyFilterRows.set([
      {
        id: 'row-1',
        optionKey: outputOption.key,
        value: 'abc-123',
      },
    ]);
    (component as any).updateBlueprintStatuses(2, ['Failed']);
    (component as any).selectedProcessStatuses.set(['Pending']);

    (component as any).apply();

    expect(dialogRef.close).toHaveBeenCalledTimes(1);
    expect(dialogRef.close.mock.calls[0][0]).toMatchObject({
      blueprintDefinitionIds: [2],
      blueprintFilters: [
        {
          blueprintDefinitionId: 2,
          statuses: ['Failed'],
        },
      ],
      processStatuses: [],
      nodeFilters: [
        {
          blueprintDefinitionId: 2,
          blueprintNodeId: 'node-b',
          statuses: ['Completed'],
        },
      ],
      propertyFilters: [
        {
          scope: 'NodeOutput',
          blueprintDefinitionId: 2,
          blueprintNodeId: 'node-b',
          propertyKey: 'token',
          propertyType: 'String',
          value: 'abc-123',
        },
      ],
      limit: 25,
    });
  });

  it('summarizes scoped blueprints nodes statuses and property filters accurately', () => {
    (component as any).selectedBlueprintIds.set([2]);
    (component as any).updateBlueprintStatuses(2, ['Failed', 'Completed']);
    (component as any).selectedNodeFilters.set([
      {
        key: '2:node-b',
        blueprintDefinitionId: 2,
        blueprintNodeId: 'node-b',
        statuses: ['Pending'],
      },
    ]);
    const outputOption = (component as any).propertyCatalog().find((option: any) => option.propertyKey === 'token');
    (component as any).propertyFilterRows.set([
      {
        id: 'row-1',
        optionKey: outputOption.key,
        propertySearchQuery: 'token (Output)',
        value: 'abc-123',
      },
    ]);

    expect((component as any).searchContextSummary()).toContain('1 blueprint');
    expect((component as any).searchContextSummary()).toContain('1 node');
    expect((component as any).searchContextSummary()).toContain('3 status selections');
    expect((component as any).searchContextSummary()).toContain('1 property filter');
  });

  it('combines execution and property blockers in the footer summary', () => {
    (component as any).createdFromInput.set('2026-04-28T10:00');
    (component as any).createdToInput.set('2026-04-27T09:00');
    (component as any).propertyFilterRows.set([
      {
        id: 'row-1',
        optionKey: null,
        propertySearchQuery: '',
        value: '',
      },
    ]);

    expect((component as any).canApply()).toBe(false);
    expect((component as any).applyBlockReason()).toBe('Fix execution window and 1 incomplete property filter.');
  });

  it('filters property picker options by query and excludes properties selected in other rows', () => {
    (component as any).selectedBlueprintIds.set([2]);
    const inputOption = (component as any).propertyCatalog().find((option: any) =>
      option.scope === 'NodeInput' && option.propertyKey === 'user_id');

    (component as any).propertyFilterRows.set([
      {
        id: 'selected-row',
        optionKey: inputOption.key,
        propertySearchQuery: 'user_id (Input)',
        value: 'u-123',
      },
      {
        id: 'search-row',
        optionKey: null,
        propertySearchQuery: 'user',
        value: '',
      },
    ]);

    const groups = (component as any).filteredPropertyOptionGroups('search-row');
    const optionKeys = groups.flatMap((group: any) => group.options.map((option: any) => option.key));

    expect(optionKeys).not.toContain(inputOption.key);
  });

  it('chooses the most relevant edit tab for restored filters', () => {
    const baseRequest: ProcessSearchRequest = {
      blueprintDefinitionIds: [],
      blueprintFilters: [],
      processStatuses: [],
      createdFrom: new Date().toISOString(),
      createdTo: new Date().toISOString(),
      nodeFilters: [],
      propertyFilters: [],
      limit: 25,
    };

    expect((component as any).initialTabIndexForRequest(baseRequest)).toBe(1);
    expect((component as any).initialTabIndexForRequest({
      ...baseRequest,
      blueprintDefinitionIds: [2],
    })).toBe(0);
    expect((component as any).initialTabIndexForRequest({
      ...baseRequest,
      propertyFilters: [
        {
          scope: 'NodeOutput',
          blueprintDefinitionId: 2,
          blueprintNodeId: 'node-b',
          propertyKey: 'token',
          propertyType: 'String',
          value: 'abc-123',
        },
      ],
    })).toBe(2);
  });
});
