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
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BlueprintEditorPageComponent } from './blueprint-editor-page.component';
import {
  BlueprintEditorService,
  BlueprintGraphDocument,
  BlueprintVersionViewModel,
  BlueprintViewModel,
} from '../domain/blueprint-editor.service';
import { BlueprintEditorStore } from '../domain/blueprint-editor.store';
import { BuilderInputService } from '../domain/builder-input.service';
import { TaskDefinitionsService } from '../domain/task-definitions.service';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';

function createTaskDefinition(): ITaskDefinition {
  return {
    id: 11,
    name: 'Lookup',
    versions: [
      {
        id: 101,
        version: '1.0.0',
        state: 'Active',
        description: 'Lookup task',
        requiredGlobalState: null,
        inputChannel: null,
        outputChannels: [],
      },
    ],
  };
}

function createBlueprintVersion(blueprintDefinitionId: number, version: number): BlueprintVersionViewModel {
  return {
    blueprintDefinitionId,
    blueprintVersionId: blueprintDefinitionId * 100 + version,
    description: null,
    version,
    startingBlueprintNodeId: 'node-1',
    tasks: [],
  };
}

function createBlueprint(blueprintDefinitionId: number, name: string, version: number): BlueprintViewModel {
  return {
    id: blueprintDefinitionId,
    name,
    description: '',
    versions: [createBlueprintVersion(blueprintDefinitionId, version)],
  };
}

describe('BlueprintEditorPageComponent', () => {
  let component: BlueprintEditorPageComponent;
  let store: BlueprintEditorStore;
  let injector: EnvironmentInjector;
  let taskDefinitions: ITaskDefinition[];
  let blueprintList: BlueprintViewModel[];

  let editorService: {
    getBlueprints: ReturnType<typeof vi.fn>;
    createBlueprintGraph: ReturnType<typeof vi.fn>;
    createBlueprintVersion: ReturnType<typeof vi.fn>;
    loadBlueprintGraph: ReturnType<typeof vi.fn>;
    hasNewerMajorMinorLane: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    taskDefinitions = [createTaskDefinition()];
    blueprintList = [];

    const savedBlueprint = createBlueprint(7, 'Saved Blueprint', 3);
    const graphDocument: BlueprintGraphDocument = {
      blueprint: savedBlueprint,
      nodes: [],
      connections: [],
    };

    editorService = {
      getBlueprints: vi.fn(() => of(blueprintList)),
      createBlueprintGraph: vi.fn(() => {
        const created = createBlueprint(17, 'Draft Blueprint', 1);
        blueprintList = [created];
        return of(created);
      }),
      createBlueprintVersion: vi.fn(() => {
        const updated = createBlueprint(7, 'Saved Blueprint', 4);
        blueprintList = [updated];
        return of(createBlueprintVersion(7, 4));
      }),
      loadBlueprintGraph: vi.fn(() => of(graphDocument)),
      hasNewerMajorMinorLane: vi.fn(() => false),
    };

    const providers: Provider[] = [
      BlueprintEditorStore,
      { provide: BlueprintEditorService, useValue: editorService },
      {
        provide: TaskDefinitionsService,
        useValue: {
          getTaskDefinitions: () => of(taskDefinitions),
        },
      },
      { provide: BuilderInputService, useValue: {} },
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
    store = injector.get(BlueprintEditorStore);
    component = runInInjectionContext(injector, () => new BlueprintEditorPageComponent());
    await Promise.resolve();
  });

  afterEach(() => {
    injector.destroy();
  });

  it('keeps new blueprints editable before the first save', () => {
    expect((component as any).isBlueprintNameReadonly()).toBe(false);

    (component as any).beginBlueprintNameEdit();
    expect((component as any).isEditingBlueprintName()).toBe(true);

    (component as any).updateBlueprintNameDraft('Draft Blueprint');
    (component as any).commitBlueprintNameEdit();

    expect((component as any).selectedBlueprintName()).toBe('Draft Blueprint');
    expect((component as any).hasBlueprintName()).toBe(true);
  });

  it('blocks save when the name is only whitespace', async () => {
    store.createNode(taskDefinitions[0], { x: 120, y: 80 });

    (component as any).beginBlueprintNameEdit();
    (component as any).updateBlueprintNameDraft('   ');
    (component as any).commitBlueprintNameEdit();

    await (component as any).save();

    expect(editorService.createBlueprintGraph).not.toHaveBeenCalled();
    expect((component as any).blueprintNameError()).toBe('Blueprint name is required.');
    expect((component as any).canSave()).toBe(false);
  });

  it('loads saved blueprints as read-only names', async () => {
    await (component as any).openBlueprint(7);

    expect((component as any).selectedBlueprintId()).toBe(7);
    expect((component as any).selectedBlueprintName()).toBe('Saved Blueprint');
    expect((component as any).isBlueprintNameReadonly()).toBe(true);

    (component as any).beginBlueprintNameEdit();
    expect((component as any).isEditingBlueprintName()).toBe(false);
  });

  it('creates a blueprint definition for the first save', async () => {
    store.createNode(taskDefinitions[0], { x: 120, y: 80 });

    (component as any).beginBlueprintNameEdit();
    (component as any).updateBlueprintNameDraft('Draft Blueprint');
    (component as any).commitBlueprintNameEdit();

    await (component as any).save();

    expect(editorService.createBlueprintGraph).toHaveBeenCalledTimes(1);
    expect(editorService.createBlueprintVersion).not.toHaveBeenCalled();
    expect((component as any).selectedBlueprintId()).toBe(17);
    expect((component as any).isBlueprintNameReadonly()).toBe(true);
  });

  it('creates a new version when saving an existing blueprint', async () => {
    blueprintList = [createBlueprint(7, 'Saved Blueprint', 3)];
    (component as any).blueprints.set(blueprintList);
    (component as any).selectedBlueprintId.set(7);
    (component as any).committedBlueprintName.set('Saved Blueprint');
    (component as any).blueprintNameDraft.set('Saved Blueprint');
    store.createNode(taskDefinitions[0], { x: 120, y: 80 });

    await (component as any).save();

    expect(editorService.createBlueprintGraph).not.toHaveBeenCalled();
    expect(editorService.createBlueprintVersion).toHaveBeenCalledTimes(1);
    expect(editorService.createBlueprintVersion.mock.calls[0][0]).toBe(7);
    expect(editorService.createBlueprintVersion.mock.calls[0][1]).toBe(3);
    expect((component as any).selectedBlueprintId()).toBe(7);
  });
});
