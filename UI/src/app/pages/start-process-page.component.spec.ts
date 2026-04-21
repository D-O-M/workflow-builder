import '@angular/compiler';
import {
  EnvironmentInjector,
  Provider,
  createEnvironmentInjector,
  runInInjectionContext,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵEffectScheduler as EffectScheduler,
} from '@angular/core';
import { FormBuilder } from '@angular/forms';
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
          requiredGlobalState: null,
          inputChannel: null,
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
    startResponse = {
      processId: 'process-123',
      blueprintId: 1,
      acceptedAt: '2026-04-21T00:00:00Z',
    };

    editorService = {
      getBlueprints: vi.fn(() => of([createBlueprint()])),
      getBlueprintById: vi.fn(() => of(createBlueprint())),
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
});
