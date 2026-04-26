import '@angular/compiler';
import {
  EnvironmentInjector,
  Provider,
  createEnvironmentInjector,
  runInInjectionContext,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵEffectScheduler as EffectScheduler,
} from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Router } from '@angular/router';
import { StartProcessPageComponent } from './start-process-page.component';
import {
  BlueprintEditorService,
  BlueprintProcessAcceptedViewModel,
  BlueprintVersionViewModel,
  BlueprintViewModel,
} from '../domain/blueprint-editor.service';
import { TaskDefinitionsService } from '../domain/task-definitions.service';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';
import { PropertyOverrideEditorState } from '../components/property-override-editor.component';

function createBlueprintVersion(): BlueprintVersionViewModel {
  return {
    blueprintDefinitionId: 1,
    blueprintVersionId: 101,
    description: null,
    version: 3,
    startingBlueprintNodeId: 'node-1',
    tasks: [
      {
        instanceId: 'node-1',
        taskDefinitionId: 11,
        allowedMajor: 1,
        allowedMinor: 0,
        resolvedVersionId: 1001,
        resolvedVersion: '1.0.0',
        position: { x: 120, y: 80 },
        designTimeInputOverrides: {},
        outgoingConnections: [],
      },
      {
        instanceId: 'node-2',
        taskDefinitionId: 12,
        allowedMajor: 1,
        allowedMinor: 0,
        resolvedVersionId: 1002,
        resolvedVersion: '1.0.0',
        position: { x: 420, y: 80 },
        designTimeInputOverrides: {},
        outgoingConnections: [],
      },
    ],
  };
}

function createBlueprint(): BlueprintViewModel {
  return {
    id: 1,
    name: 'Runtime Override Blueprint',
    description: '',
    versions: [createBlueprintVersion()],
  };
}

function createBlueprints(): BlueprintViewModel[] {
  return [
    createBlueprint(),
    {
      ...createBlueprint(),
      id: 2,
      name: 'Showcase Town Weekend Recommendation',
    },
  ];
}

function createTaskDefinitions(): ITaskDefinition[] {
  return [
    {
      id: 11,
      name: 'Starter',
      versions: [
        {
          id: 1001,
          version: '1.0.0',
          state: 'Active',
          description: 'Start node',
          requiredGlobalState: {
            id: 1,
            name: 'GlobalState',
            properties: [
              { name: 'LaunchEnabled', kind: 'Boolean', required: false, enumValues: [] },
              { name: 'LaunchCount', kind: 'Integer', required: false, enumValues: [] },
              { name: 'TargetScore', kind: 'Decimal', required: false, enumValues: [] },
              { name: 'OptionalNote', kind: 'String', required: false, enumValues: [] },
            ],
          },
          inputChannel: {
            id: 2,
            name: 'Input',
            properties: [
              { name: 'Town', kind: 'String', required: false, enumValues: [] },
              { name: 'StartDate', kind: 'Date', required: false, enumValues: [] },
              { name: 'LaunchMode', kind: 'Enum', required: false, enumValues: ['DryRun', 'Live'] },
            ],
          },
          outputChannels: [
            {
              name: 'Success',
              properties: {
                town: 'String',
              },
            },
          ],
        },
      ],
    },
    {
      id: 12,
      name: 'Recommend',
      versions: [
        {
          id: 1002,
          version: '1.0.0',
          state: 'Active',
          description: 'Recommendation node',
          requiredGlobalState: null,
          inputChannel: {
            name: 'Input',
            properties: {
              town: 'String',
              holiday: 'Boolean',
            },
          },
          outputChannels: [],
        },
      ],
    },
  ];
}

describe('StartProcessPageComponent', () => {
  let component: StartProcessPageComponent;
  let injector: EnvironmentInjector;
  let startResponse: BlueprintProcessAcceptedViewModel;
  let taskDefinitions: ITaskDefinition[];
  let blueprints: BlueprintViewModel[];

  let editorService: {
    getBlueprints: ReturnType<typeof vi.fn>;
    getBlueprintById: ReturnType<typeof vi.fn>;
    startProcess: ReturnType<typeof vi.fn>;
  };
  let router: {
    navigate: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    taskDefinitions = createTaskDefinitions();
    blueprints = createBlueprints();
    startResponse = {
      processId: 'process-123',
      blueprintId: 1,
      acceptedAt: '2026-04-21T00:00:00Z',
    };

    editorService = {
      getBlueprints: vi.fn(() => of(blueprints)),
      getBlueprintById: vi.fn((blueprintId: number) =>
        of(blueprints.find((blueprint) => blueprint.id === blueprintId) ?? blueprints[0])),
      startProcess: vi.fn(() => of(startResponse)),
    };

    router = {
      navigate: vi.fn(() => Promise.resolve(true)),
    };

    const providers: Provider[] = [
      FormBuilder,
      { provide: BlueprintEditorService, useValue: editorService },
      {
        provide: TaskDefinitionsService,
        useValue: {
          getTaskDefinitions: () => of(taskDefinitions),
        },
      },
      { provide: Router, useValue: router },
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
    component = runInInjectionContext(injector, () => new StartProcessPageComponent());
    await Promise.resolve();
  });

  afterEach(() => {
    injector.destroy();
  });

  async function flushSignals(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
  }

  it('serializes structured runtime overrides into the start payload', async () => {
    component.form.controls.blueprintId.setValue(1);
    await flushSignals();

    (component as any).onRuntimeOverrideStateChanged({
      editorId: 'node-2',
      overrides: {
        town: 'London',
        holiday: true,
      },
      valid: true,
    } satisfies PropertyOverrideEditorState);

    await component.execute();

    expect(editorService.startProcess).toHaveBeenCalledWith(1, {
      globalState: {},
      initialTaskInput: {},
      nodeOverrides: {
        'node-2': {
          town: 'London',
          holiday: true,
        },
      },
    });
    expect(router.navigate).toHaveBeenCalled();
  });

  it('starts the process when runtime overrides are left empty', async () => {
    component.form.controls.blueprintId.setValue(1);
    await flushSignals();

    await component.execute();

    expect(editorService.startProcess).toHaveBeenCalledWith(1, {
      globalState: {},
      initialTaskInput: {},
      nodeOverrides: {},
    });
    expect((component as any).executeErrorMessage()).toBeNull();
  });

  it('serializes initialization and global state controls as typed JSON values', async () => {
    component.form.controls.globalState.addControl('LaunchEnabled', new FormControl('false'));
    component.form.controls.globalState.addControl('LaunchCount', new FormControl('4'));
    component.form.controls.globalState.addControl('TargetScore', new FormControl('12.5'));
    component.form.controls.globalState.addControl('OptionalNote', new FormControl(''));
    component.form.controls.globalState.patchValue({
      LaunchEnabled: 'false',
      LaunchCount: '4',
      TargetScore: '12.5',
      OptionalNote: '',
    });
    component.form.controls.initialTaskInput.addControl('Town', new FormControl('York'));
    component.form.controls.initialTaskInput.addControl('StartDate', new FormControl('2026-04-25'));
    component.form.controls.initialTaskInput.addControl('LaunchMode', new FormControl('DryRun'));
    component.form.controls.initialTaskInput.patchValue({
      Town: 'York',
      StartDate: '2026-04-25',
      LaunchMode: 'DryRun',
    });

    expect((component as any).serializeFieldGroup(
      [
        { key: 'LaunchEnabled', label: 'Launch enabled', kind: 'boolean', required: false },
        { key: 'LaunchCount', label: 'Launch count', kind: 'integer', required: false },
        { key: 'TargetScore', label: 'Target score', kind: 'decimal', required: false },
        { key: 'OptionalNote', label: 'Optional note', kind: 'string', required: false },
      ],
      component.form.controls.globalState,
    )).toEqual({
        LaunchEnabled: false,
        LaunchCount: 4,
        TargetScore: 12.5,
      });
    expect((component as any).serializeFieldGroup(
      [
        { key: 'Town', label: 'Town', kind: 'string', required: false },
        { key: 'StartDate', label: 'Start date', kind: 'date', required: false },
        { key: 'LaunchMode', label: 'Launch mode', kind: 'enum', required: false, options: ['DryRun', 'Live'] },
      ],
      component.form.controls.initialTaskInput,
    )).toEqual({
        Town: 'York',
        StartDate: '2026-04-25',
        LaunchMode: 'DryRun',
    });
  });

  it('blocks execution when a structured runtime override editor is invalid', async () => {
    component.form.controls.blueprintId.setValue(1);
    await flushSignals();

    (component as any).onRuntimeOverrideStateChanged({
      editorId: 'node-2',
      overrides: {
        town: '',
      },
      valid: false,
    } satisfies PropertyOverrideEditorState);

    await component.execute();

    expect(editorService.startProcess).not.toHaveBeenCalled();
    expect((component as any).executeErrorMessage()).toBe(
      'Complete or clear any runtime overrides before starting the process.',
    );
  });

  it('does not resynchronize runtime override state when only editor state changes', async () => {
    component.form.controls.blueprintId.setValue(1);
    await flushSignals();

    const beforeUpdate = (component as any).runtimeOverrideStates();

    (component as any).onRuntimeOverrideStateChanged({
      editorId: 'node-2',
      overrides: {
        town: 'York',
      },
      valid: true,
    } satisfies PropertyOverrideEditorState);

    const afterUpdate = (component as any).runtimeOverrideStates();
    await flushSignals();
    const afterSignalsSettle = (component as any).runtimeOverrideStates();

    expect(afterUpdate).not.toBe(beforeUpdate);
    expect(afterSignalsSettle).toBe(afterUpdate);
    expect(afterSignalsSettle['node-2'].overrides).toEqual({
      town: 'York',
    });
  });

  it('filters blueprint suggestions using the picker query', async () => {
    (component as any).blueprintSearch.setValue('showcase');
    await flushSignals();

    expect((component as any).pickerSuggestions()).toEqual([
      {
        blueprint: blueprints[1],
        isActive: false,
      },
    ]);
  });

  it('selects a blueprint from the picker suggestions and switches to selected mode', async () => {
    (component as any).onBlueprintSuggestionSelected({
      option: {
        value: 2,
      },
    });
    await flushSignals();

    expect(component.form.controls.blueprintId.value).toBe(2);
    expect((component as any).pickerMode()).toBe('selected');
    expect((component as any).pickerDisplayName()).toBe('Showcase Town Weekend Recommendation');
  });

  it('recomputes the initialisation step gate when form validity changes', async () => {
    component.form.controls.blueprintId.setValue(1);
    await flushSignals();

    component.form.controls.initialTaskInput.addControl(
      'requestedTownName',
      new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    );
    await flushSignals();

    expect((component as any).canAdvanceFromInitialisation()).toBe(false);

    component.form.controls.initialTaskInput.controls['requestedTownName'].setValue('New Alexandria');
    await flushSignals();

    expect((component as any).canAdvanceFromInitialisation()).toBe(true);
  });

  it('resets the wizard back to the first step when the blueprint changes', async () => {
    (component as any).onWizardStepIndexChanged(2);
    (component as any).onBlueprintSuggestionSelected({
      option: {
        value: 2,
      },
    });
    await flushSignals();

    expect((component as any).wizardStepIndex()).toBe(0);
  });

  it('clears the wizard state when the configuration is reset', async () => {
    component.form.controls.blueprintId.setValue(1);
    await flushSignals();

    (component as any).onWizardStepIndexChanged(2);
    component.resetConfiguration();

    expect((component as any).wizardStepIndex()).toBe(0);
    expect(component.form.controls.blueprintId.value).toBeNull();
    expect((component as any).pickerMode()).toBe('search');
  });
});
