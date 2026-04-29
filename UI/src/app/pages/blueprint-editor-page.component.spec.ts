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
import { ITaskDefinition, ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

function createTaskDefinition(
  overrides?: Partial<ITaskDefinition> & {
    versionOverrides?: Partial<ITaskDefinition['versions'][number]>;
  },
): ITaskDefinition {
  return {
    id: overrides?.id ?? 11,
    name: overrides?.name ?? 'Lookup',
    versions: [
      {
        id: overrides?.versionOverrides?.id ?? 101,
        version: overrides?.versionOverrides?.version ?? '1.0.0',
        state: overrides?.versionOverrides?.state ?? 'Active',
        description: overrides?.versionOverrides?.description ?? 'Lookup task',
        requiredGlobalState: overrides?.versionOverrides?.requiredGlobalState ?? null,
        inputChannel: overrides?.versionOverrides?.inputChannel ?? null,
        outputChannels: overrides?.versionOverrides?.outputChannels ?? [],
      },
    ],
  };
}

function createSourceTaskDefinition(): ITaskDefinition {
  return {
    id: 21,
    name: 'Lookup',
    versions: [
      {
        id: 201,
        version: '1.0.0',
        state: 'Active',
        description: 'Lookup task',
        requiredGlobalState: null,
        inputChannel: null,
        outputChannels: [
          {
            name: 'Success',
            properties: {
              town: 'String',
              holiday: 'String',
            },
          },
        ],
      },
    ],
  };
}

function createTargetTaskDefinition(): ITaskDefinition {
  return {
    id: 22,
    name: 'Recommend',
    versions: [
      {
        id: 202,
        version: '1.0.0',
        state: 'Active',
        description: 'Recommendation task',
        requiredGlobalState: null,
        inputChannel: {
          name: 'Input',
          properties: {
            mappedTown: 'String',
            holiday: 'String',
          },
        },
        outputChannels: [],
      },
    ],
  };
}

function createValidationTaskDefinition(
  id: number,
  name: string,
  options?: {
    inputProperties?: ITaskPropertyDefinition[];
    outputChannels?: Record<string, ITaskPropertyDefinition[]>;
  },
): ITaskDefinition {
  return {
    id,
    name,
    versions: [
      {
        id: id * 100,
        version: '1.0.0',
        state: 'Active',
        description: `${name} task`,
        requiredGlobalState: null,
        inputChannel: options?.inputProperties
          ? {
            name: 'Input',
            properties: options.inputProperties,
          }
          : null,
        outputChannels: Object.entries(options?.outputChannels ?? {}).map(([channelName, properties]) => ({
          name: channelName,
          properties,
        })),
      },
    ],
  };
}

function property(name: string, required = false): ITaskPropertyDefinition {
  return {
    name,
    kind: 'String',
    required,
    enumValues: [],
  };
}

function createBlueprintVersion(blueprintDefinitionId: number, version: number): BlueprintVersionViewModel {
  return {
    blueprintDefinitionId,
    blueprintVersionId: blueprintDefinitionId * 100 + version,
    description: null,
    version,
    createdAt: '2026-04-28T12:00:00Z',
    startingBlueprintNodeId: 'node-1',
    tasks: [],
  };
}

function createBlueprint(
  blueprintDefinitionId: number,
  name: string,
  versions: number | number[],
): BlueprintViewModel {
  const versionList = Array.isArray(versions) ? versions : [versions];
  return {
    id: blueprintDefinitionId,
    name,
    description: '',
    versions: versionList.map((version) => createBlueprintVersion(blueprintDefinitionId, version)),
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
    taskDefinitions = [
      createTaskDefinition(),
      createTaskDefinition({
        id: 12,
        name: 'Webhook Listener',
        versionOverrides: {
          id: 102,
          description: 'HTTP endpoint',
        },
      }),
      createTaskDefinition({
        id: 13,
        name: 'Email Dispatch',
        versionOverrides: {
          id: 103,
          description: 'Send notifications',
          inputChannel: {
            name: 'Input',
            properties: {},
          },
        },
      }),
    ];
    blueprintList = [];

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
      loadBlueprintGraph: vi.fn((definitionId: number) => {
        const blueprint = blueprintList.find((candidate) => candidate.id === definitionId)
          ?? createBlueprint(definitionId, `Blueprint ${definitionId}`, 1);
        const graphDocument: BlueprintGraphDocument = {
          blueprint,
          nodes: [],
          connections: [],
        };
        return of(graphDocument);
      }),
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
      {
        provide: MatDialog,
        useValue: {
          open: vi.fn(() => ({
            close: vi.fn(),
            afterClosed: () => of(undefined),
          })),
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
    (component as any).newBlueprint();

    expect((component as any).pickerMode()).toBe('draft');
    expect((component as any).isBlueprintNameReadonly()).toBe(false);

    (component as any).beginBlueprintNameEdit();
    expect((component as any).isEditingBlueprintName()).toBe(true);

    (component as any).updateBlueprintNameDraft('My Draft Blueprint');
    (component as any).commitBlueprintNameEdit();

    expect((component as any).pickerMode()).toBe('draft');
    expect((component as any).selectedBlueprintName()).toBe('My Draft Blueprint');
    expect((component as any).hasBlueprintName()).toBe(true);
  });

  it('blocks save when the name is only whitespace', async () => {
    (component as any).newBlueprint();
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
    blueprintList = [createBlueprint(7, 'Saved Blueprint', 3)];
    (component as any).blueprints.set(blueprintList);

    await (component as any).openBlueprint(7);

    expect((component as any).selectedBlueprintId()).toBe(7);
    expect((component as any).selectedBlueprintName()).toBe('Saved Blueprint');
    expect((component as any).isBlueprintNameReadonly()).toBe(true);
    expect((component as any).pickerMode()).toBe('selected');

    (component as any).beginBlueprintNameEdit();
    expect((component as any).isEditingBlueprintName()).toBe(false);
  });

  it('creates a blueprint definition for the first save', async () => {
    (component as any).newBlueprint();
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

  it('blocks persistence and pulses the error badge when validation has errors', async () => {
    const sourceTask = createValidationTaskDefinition(41, 'Source', {
      outputChannels: {
        Success: [property('other')],
      },
    });
    const targetTask = createValidationTaskDefinition(42, 'Target', {
      inputProperties: [property('town', true)],
    });
    taskDefinitions = [sourceTask, targetTask];
    (component as any).newBlueprint();
    (component as any).beginBlueprintNameEdit();
    (component as any).updateBlueprintNameDraft('Invalid Blueprint');
    (component as any).commitBlueprintNameEdit();
    const source = store.createNode(sourceTask, { x: 120, y: 80 });
    const target = store.createNode(targetTask, { x: 540, y: 80 });
    store.createConnection(source.id, target.id, source.outputAnchors()[0]!.index, 'left', target.inputAnchorSnap()!.pos);

    await (component as any).save();
    await Promise.resolve();

    expect(editorService.createBlueprintGraph).not.toHaveBeenCalled();
    expect(editorService.createBlueprintVersion).not.toHaveBeenCalled();
    expect((component as any).validationSummary().errorCount).toBeGreaterThan(0);
    expect((component as any).validationErrorPulse()).toBe(true);
  });

  it('focuses targetable validation issues on the canvas and closes the validation menu', () => {
    const sourceTask = createValidationTaskDefinition(45, 'Source', {
      outputChannels: {
        Success: [property('other')],
      },
    });
    const targetTask = createValidationTaskDefinition(46, 'Target', {
      inputProperties: [property('town', true)],
    });
    taskDefinitions = [sourceTask, targetTask];
    (component as any).newBlueprint();
    (component as any).beginBlueprintNameEdit();
    (component as any).updateBlueprintNameDraft('Invalid Blueprint');
    (component as any).commitBlueprintNameEdit();
    const source = store.createNode(sourceTask, { x: 120, y: 80 });
    const target = store.createNode(targetTask, { x: 540, y: 80 });
    store.createConnection(source.id, target.id, source.outputAnchors()[0]!.index, 'left', target.inputAnchorSnap()!.pos);
    const issue = (component as any).validationErrors().find((candidate: { code: string }) =>
      candidate.code === 'node.input.requiredMissing');
    const revealValidationFocus = vi.fn();
    const focusCanvas = vi.fn();
    const closeMenu = vi.fn();
    Object.defineProperty(component as any, 'canvasViewport', {
      value: () => ({ revealValidationFocus, focusCanvas }),
      configurable: true,
    });
    Object.defineProperty(component as any, 'validationMenuTriggers', {
      value: () => [{ closeMenu }],
      configurable: true,
    });
    (component as any).scheduleUiTask = (task: () => void) => task();

    (component as any).focusValidationIssue(issue);

    expect((component as any).focusedValidationIssueId()).toBe(issue.id);
    expect(closeMenu).toHaveBeenCalled();
    expect(revealValidationFocus).toHaveBeenCalledWith(expect.objectContaining({
      issueId: issue.id,
      severity: 'error',
      nodeId: target.id,
      propertyName: 'town',
      channelName: 'Input',
      side: 'left',
    }));
  });

  it('allows saving when validation only has warnings', async () => {
    const sourceTask = createValidationTaskDefinition(43, 'Source', {
      outputChannels: {
        Success: [property('town'), property('temperature')],
        Audit: [property('event')],
      },
    });
    const targetTask = createValidationTaskDefinition(44, 'Target', {
      inputProperties: [property('town', false)],
    });
    taskDefinitions = [sourceTask, targetTask];
    (component as any).newBlueprint();
    (component as any).beginBlueprintNameEdit();
    (component as any).updateBlueprintNameDraft('Warning Blueprint');
    (component as any).commitBlueprintNameEdit();
    const source = store.createNode(sourceTask, { x: 120, y: 80 });
    const target = store.createNode(targetTask, { x: 540, y: 80 });
    const connection = store.createConnection(source.id, target.id, source.outputAnchors()[0]!.index, 'left', target.inputAnchorSnap()!.pos)!;
    store.updateConnectionPropertyMappings(connection.id, [{ sourceProperty: 'town', targetProperty: 'town' }]);

    expect((component as any).validationSummary().errorCount).toBe(0);
    expect((component as any).validationSummary().warningCount).toBeGreaterThan(0);

    await (component as any).save();

    expect(editorService.createBlueprintGraph).toHaveBeenCalledTimes(1);
    expect(editorService.createBlueprintVersion).not.toHaveBeenCalled();
  });

  it('creates a new version when saving an existing blueprint', async () => {
    blueprintList = [createBlueprint(7, 'Saved Blueprint', 3)];
    (component as any).blueprints.set(blueprintList);
    (component as any).selectedBlueprintId.set(7);
    (component as any).committedBlueprintName.set('Saved Blueprint');
    (component as any).blueprintNameDraft.set('Saved Blueprint');
    (component as any).pickerMode.set('selected');
    store.createNode(taskDefinitions[0], { x: 120, y: 80 });

    await (component as any).save();

    expect(editorService.createBlueprintGraph).not.toHaveBeenCalled();
    expect(editorService.createBlueprintVersion).toHaveBeenCalledTimes(1);
    expect(editorService.createBlueprintVersion.mock.calls[0][0]).toBe(7);
    expect(editorService.createBlueprintVersion.mock.calls[0][1]).toBe(3);
    expect((component as any).selectedBlueprintId()).toBe(7);
  });

  it('defaults the version picker to the latest blueprint version when opening a blueprint', async () => {
    blueprintList = [createBlueprint(7, 'Saved Blueprint', [5, 4, 3])];
    (component as any).blueprints.set(blueprintList);

    await (component as any).openBlueprint(7);

    expect((component as any).showVersionPicker()).toBe(true);
    expect((component as any).selectedBlueprintVersionId()).toBe(705);
    expect((component as any).selectedBlueprintVersion().version).toBe(5);
  });

  it('loads the chosen blueprint version when switching from the version picker', async () => {
    blueprintList = [createBlueprint(7, 'Saved Blueprint', [5, 4, 3])];
    (component as any).blueprints.set(blueprintList);

    await (component as any).openBlueprint(7);
    await (component as any).openBlueprint(7, 704);

    expect(editorService.loadBlueprintGraph).toHaveBeenLastCalledWith(7, taskDefinitions, 704);
    expect((component as any).selectedBlueprintVersionId()).toBe(704);
    expect((component as any).selectedBlueprintVersion().version).toBe(4);
  });

  it('formats blueprint version creation dates as day month year', () => {
    const version = createBlueprintVersion(7, 4);

    expect((component as any).formatBlueprintVersionCreatedDate(version)).toBe('28/04/2026');
  });

  it('uses the selected blueprint version as the save base version', async () => {
    blueprintList = [createBlueprint(7, 'Saved Blueprint', [5, 4, 3])];
    (component as any).blueprints.set(blueprintList);
    (component as any).selectedBlueprintId.set(7);
    (component as any).selectedBlueprintVersionId.set(704);
    (component as any).committedBlueprintName.set('Saved Blueprint');
    (component as any).blueprintNameDraft.set('Saved Blueprint');
    (component as any).pickerMode.set('selected');
    store.createNode(taskDefinitions[0], { x: 120, y: 80 });

    await (component as any).save();

    expect(editorService.createBlueprintVersion).toHaveBeenCalledTimes(1);
    expect(editorService.createBlueprintVersion.mock.calls[0][1]).toBe(4);
  });

  it('filters blueprint suggestions from the picker search query', async () => {
    blueprintList = [
      createBlueprint(4, 'Alpha Flow', 1),
      createBlueprint(7, 'Saved Blueprint', 3),
      createBlueprint(9, 'Beta Process', 1),
    ];
    (component as any).blueprints.set(blueprintList);
    (component as any).blueprintSearch.setValue('alpha');
    await Promise.resolve();

    expect((component as any).pickerSuggestions().map((suggestion: any) => suggestion.blueprint.name)).toEqual([
      'Alpha Flow',
    ]);
  });

  it('keeps blueprint suggestions stable when the picker temporarily holds an option value', () => {
    blueprintList = [
      createBlueprint(4, 'Alpha Flow', 1),
      createBlueprint(7, 'Saved Blueprint', 3),
    ];
    (component as any).blueprints.set(blueprintList);
    (component as any).blueprintSearch.setValue(7 as never);

    expect((component as any).pickerSuggestions().map((suggestion: any) => suggestion.blueprint.name)).toEqual([
      'Alpha Flow',
      'Saved Blueprint',
    ]);
  });

  it('pins the active blueprint to the top when reopening picker search from a selection', () => {
    blueprintList = [
      createBlueprint(4, 'Alpha Flow', 1),
      createBlueprint(7, 'Saved Blueprint', 3),
      createBlueprint(9, 'Beta Process', 1),
    ];
    (component as any).blueprints.set(blueprintList);
    (component as any).selectedBlueprintId.set(7);
    (component as any).committedBlueprintName.set('Saved Blueprint');
    (component as any).blueprintNameDraft.set('Saved Blueprint');
    (component as any).pickerMode.set('selected');

    (component as any).beginPickerSearchFromTrigger();

    expect((component as any).pickerMode()).toBe('search');
    expect((component as any).pickerSuggestions()[0].blueprint.id).toBe(7);
    expect((component as any).pickerSuggestions()[0].isActive).toBe(true);
  });

  it('lists the active blueprint versions beneath the active picker option', () => {
    blueprintList = [
      createBlueprint(4, 'Alpha Flow', 1),
      createBlueprint(7, 'Saved Blueprint', [5, 4, 3]),
      createBlueprint(9, 'Beta Process', 1),
    ];
    (component as any).blueprints.set(blueprintList);
    (component as any).selectedBlueprintId.set(7);
    (component as any).selectedBlueprintVersionId.set(704);
    (component as any).committedBlueprintName.set('Saved Blueprint');
    (component as any).blueprintNameDraft.set('Saved Blueprint');
    (component as any).pickerMode.set('selected');

    (component as any).beginPickerSearchFromTrigger();

    const options = (component as any).pickerOptions();
    expect(options.map((option: any) => option.kind === 'version'
      ? `${option.kind}:${option.version.version}:${option.isSelected}`
      : `${option.kind}:${option.blueprint.id}`,
    )).toEqual([
      'blueprint:7',
      'version:5:false',
      'version:4:true',
      'version:3:false',
      'blueprint:4',
      'blueprint:9',
    ]);
  });

  it('enters draft mode with Draft Blueprint and excludes it from existing suggestions', () => {
    blueprintList = [
      createBlueprint(4, 'Alpha Flow', 1),
      createBlueprint(7, 'Saved Blueprint', 3),
    ];
    (component as any).blueprints.set(blueprintList);

    (component as any).newBlueprint();

    expect((component as any).pickerMode()).toBe('draft');
    expect((component as any).pickerDisplayName()).toBe('Draft Blueprint');

    (component as any).beginPickerSearchFromDraftChevron();

    expect((component as any).pickerMode()).toBe('search');
    expect((component as any).pickerSuggestions().map((suggestion: any) => suggestion.blueprint.name)).toEqual([
      'Alpha Flow',
      'Saved Blueprint',
    ]);
  });

  it('opens and closes the plugin drawer explicitly', () => {
    expect((component as any).drawerCollapsed()).toBe(true);

    (component as any).openDrawer();
    expect((component as any).drawerCollapsed()).toBe(false);

    (component as any).closeDrawer();
    expect((component as any).drawerCollapsed()).toBe(true);
  });

  it('uses the stitch canvas backdrop in the builder workspace', () => {
    expect((component as any).canvasBackgroundEffect).toBe('stitch');
  });

  it('filters the flat plugin library cards from the drawer search query', async () => {
    (component as any).pluginLibrarySearch.setValue('webhook');
    await Promise.resolve();

    expect((component as any).filteredTaskLibraryItems().map((item: any) => item.task.name)).toEqual([
      'Webhook Listener',
    ]);
  });

  it('returns no plugin cards when the drawer search has no matches', async () => {
    (component as any).pluginLibrarySearch.setValue('does-not-exist');
    await Promise.resolve();

    expect((component as any).filteredTaskLibraryItems()).toEqual([]);
  });

  it('adds a task to the canvas from the flat plugin library list', () => {
    const item = (component as any).taskLibraryItems().find((candidate: any) => candidate.task?.name === 'Webhook Listener');

    (component as any).addLibraryItemFromDrawer(item);

    expect(store.nodes().length).toBe(1);
    expect(store.nodes()[0].name).toBe('Webhook Listener');
  });

  it('adds Collection Iterator from the library list', () => {
    const item = (component as any).taskLibraryItems().find((candidate: any) => candidate.kind === 'collectionIterator');

    (component as any).addLibraryItemFromDrawer(item);

    expect(store.nodes().length).toBe(1);
    expect(store.nodes()[0].name).toBe('Collection Iterator');
    expect(store.nodes()[0].inputProperties['items']).toBe('String[]');
    expect(store.nodes()[0].outputChannelProperties['Item']['item']).toBe('String');
  });

  it('switches drawer overlay metadata based on viewport size', () => {
    (component as any).compactDrawerViewport.set(false);
    expect((component as any).pluginLibraryDrawerMode()).toBe('overlay');
    expect((component as any).pluginLibraryHasBackdrop()).toBe(false);

    (component as any).compactDrawerViewport.set(true);
    expect((component as any).pluginLibraryDrawerMode()).toBe('overlay-with-backdrop');
    expect((component as any).pluginLibraryHasBackdrop()).toBe(true);
  });

  it('opens node settings in the shared drawer', () => {
    const node = store.createNode(createTargetTaskDefinition(), { x: 120, y: 80 });

    (component as any).openNodeSettings(node.id);

    expect((component as any).activeDrawer()).toBe('node-settings');
    expect((component as any).nodeSettingsNode()?.id).toBe(node.id);
    expect(store.selectedNodeIds()).toEqual([node.id]);
  });

  it('adds and removes static override fields from node settings', async () => {
    const node = store.createNode(createTargetTaskDefinition(), { x: 120, y: 80 });
    (component as any).openNodeSettings(node.id);

    (component as any).addStaticOverride('holiday');
    await Promise.resolve();

    expect((component as any).staticOverrideFields().map((field: any) => field.key)).toEqual(['holiday']);
    expect((component as any).availableStaticOverrideProperties().map((property: any) => property.name)).toEqual(['mappedTown']);

    (component as any).staticOverrideForm.get('holiday')?.setValue('bank holiday');
    await Promise.resolve();

    expect(store.nodes().find((candidate) => candidate.id === node.id)?.designTimeInputOverrides).toEqual({
      holiday: 'bank holiday',
    });

    (component as any).removeStaticOverride('holiday');

    expect((component as any).staticOverrideFields()).toEqual([]);
    expect(store.nodes().find((candidate) => candidate.id === node.id)?.designTimeInputOverrides).toEqual({});
  });

  it('uses latest patch options per minor version in node settings', () => {
    const task: ITaskDefinition = {
      id: 30,
      name: 'Versioned',
      versions: [
        {
          id: 301,
          version: '1.0.0',
          state: 'Active',
          description: null,
          requiredGlobalState: null,
          inputChannel: null,
          outputChannels: [],
        },
        {
          id: 302,
          version: '1.0.3',
          state: 'Active',
          description: null,
          requiredGlobalState: null,
          inputChannel: null,
          outputChannels: [],
        },
        {
          id: 303,
          version: '1.1.0',
          state: 'Active',
          description: null,
          requiredGlobalState: null,
          inputChannel: null,
          outputChannels: [],
        },
      ],
    };
    taskDefinitions.splice(0, taskDefinitions.length, task);
    const node = store.createNode(task, { x: 120, y: 80 });

    (component as any).openNodeSettings(node.id);

    expect((component as any).nodeVersionOptions().map((option: any) => option.version.version)).toEqual([
      '1.1.0',
      '1.0.3',
    ]);
  });
});
