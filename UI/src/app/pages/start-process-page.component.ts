import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { catchError, distinctUntilChanged, firstValueFrom, forkJoin, map, of, startWith } from 'rxjs';
import { BlueprintEditorService, BlueprintViewModel, BlueprintVersionViewModel } from '../domain/blueprint-editor.service';
import { DynamicFieldDefinition, DynamicFormFieldComponent } from '../components/dynamic-form-field.component';
import {
  PropertyOverrideEditorComponent,
  PropertyOverrideEditorState,
} from '../components/property-override-editor.component';
import { ITaskDefinition, ITaskVersion } from '../interfaces/ITaskDefinition';
import {
  createRequestStateStream,
  errorRequestState,
  idleRequestState,
  isErrorRequestState,
  isReadyRequestState,
  loadingRequestState,
  readyRequestState,
} from '../domain/request-state';
import { TaskDefinitionsService } from '../domain/task-definitions.service';
import { TaskPropertyType } from '../domain/node-model';
import {
  createTaskPropertyControl,
  createTaskPropertyFieldDefinitions,
} from '../domain/task-property-form-utils';

interface RuntimeOverrideNode {
  instanceId: string;
  name: string;
  inputProperties: Record<string, TaskPropertyType>;
  propertyCount: number;
}

interface StartProcessInitialData {
  blueprints: BlueprintViewModel[];
  taskDefinitions: ITaskDefinition[];
}

type RuntimeOverrideStateMap = Record<string, PropertyOverrideEditorState>;

function buildRuntimeOverrideStateMap(
  nodes: RuntimeOverrideNode[],
  currentStates: RuntimeOverrideStateMap,
): RuntimeOverrideStateMap {
  return Object.fromEntries(
    nodes.map((node) => [
      node.instanceId,
      currentStates[node.instanceId] ?? {
        editorId: node.instanceId,
        overrides: {},
        valid: true,
      },
    ]),
  );
}

function runtimeOverrideStateMapsEqual(
  left: RuntimeOverrideStateMap,
  right: RuntimeOverrideStateMap,
): boolean {
  const leftKeys = Object.keys(left);
  const rightKeys = Object.keys(right);
  if (leftKeys.length !== rightKeys.length) {
    return false;
  }

  return leftKeys.every((key) => runtimeOverrideStatesEqual(left[key], right[key]));
}

function runtimeOverrideStatesEqual(
  left: PropertyOverrideEditorState | undefined,
  right: PropertyOverrideEditorState | undefined,
): boolean {
  if (!left || !right) {
    return left === right;
  }

  return left.editorId === right.editorId
    && left.valid === right.valid
    && overridesEqual(left.overrides, right.overrides);
}

function overridesEqual(
  left: Record<string, unknown>,
  right: Record<string, unknown>,
): boolean {
  const leftKeys = Object.keys(left);
  const rightKeys = Object.keys(right);
  if (leftKeys.length !== rightKeys.length) {
    return false;
  }

  return leftKeys.every((key) => Object.is(left[key], right[key]));
}

@Component({
  selector: 'app-start-process-page',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatChipsModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatIconModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    DynamicFormFieldComponent,
    PropertyOverrideEditorComponent,
  ],
  templateUrl: './start-process-page.component.html',
  styleUrl: './start-process-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StartProcessPageComponent {
  private readonly fb = inject(FormBuilder);
  private readonly blueprintService = inject(BlueprintEditorService);
  private readonly taskDefinitionsService = inject(TaskDefinitionsService);
  private readonly router = inject(Router);

  protected readonly executing = signal(false);
  protected readonly executeErrorMessage = signal<string | null>(null);
  protected readonly successMessage = signal<string | null>(null);
  protected readonly advancedOverridesExpanded = signal(false);
  protected readonly runtimeOverrideStates = signal<Record<string, PropertyOverrideEditorState>>({});

  protected readonly form = this.fb.group({
    blueprintId: this.fb.control<number | null>(null, Validators.required),
    globalState: this.fb.group({}),
    initialTaskInput: this.fb.group({}),
  });

  private readonly initialDataState = toSignal(
    forkJoin({
      blueprints: this.blueprintService.getBlueprints(),
      taskDefinitions: this.taskDefinitionsService.getTaskDefinitions(),
    }).pipe(
      map((data) => readyRequestState<StartProcessInitialData>(data)),
      startWith(loadingRequestState<StartProcessInitialData>()),
      catchError(() => of(errorRequestState<StartProcessInitialData>('Unable to load blueprints and task definitions.'))),
    ),
    { initialValue: loadingRequestState<StartProcessInitialData>() },
  );

  private readonly selectedBlueprintId = toSignal(
    this.form.controls.blueprintId.valueChanges.pipe(startWith(this.form.controls.blueprintId.value)),
    { initialValue: this.form.controls.blueprintId.value },
  );

  private readonly selectedBlueprintState = toSignal(
    createRequestStateStream(
      toObservable(this.selectedBlueprintId).pipe(distinctUntilChanged()),
      (blueprintId) => this.blueprintService.getBlueprintById(blueprintId),
      'Unable to load the selected blueprint.',
    ),
    { initialValue: idleRequestState<BlueprintViewModel>() },
  );

  protected readonly loading = computed(() => this.initialDataState().status === 'loading');
  protected readonly blueprintLoading = computed(() => this.selectedBlueprintState().status === 'loading');
  protected readonly blueprints = computed<BlueprintViewModel[]>(() => {
    const state = this.initialDataState();
    return isReadyRequestState(state) ? state.data.blueprints : [];
  });
  protected readonly taskDefinitions = computed<ITaskDefinition[]>(() => {
    const state = this.initialDataState();
    return isReadyRequestState(state) ? state.data.taskDefinitions : [];
  });
  protected readonly selectedBlueprint = computed<BlueprintViewModel | null>(() => {
    const state = this.selectedBlueprintState();
    return isReadyRequestState(state) ? state.data : null;
  });
  protected readonly selectedBlueprintVersion = computed<BlueprintVersionViewModel | null>(() =>
    this.selectedBlueprint()?.versions[0] ?? null,
  );

  private readonly taskDefinitionById = computed(() => new Map(
    this.taskDefinitions().map((definition) => [definition.id, definition] as const),
  ));

  private readonly taskVersionById = computed(() => new Map(
    this.taskDefinitions().flatMap((definition) =>
      definition.versions.map((version) => [version.id, version] as const)),
  ));

  private readonly startingTask = computed(() => {
    const blueprintVersion = this.selectedBlueprintVersion();
    if (!blueprintVersion) {
      return null;
    }

    return blueprintVersion.tasks.find(
      (task) => task.instanceId === blueprintVersion.startingBlueprintNodeId,
    ) ?? null;
  });

  protected readonly firstTaskVersion = computed<ITaskVersion | null>(() => {
    const firstTask = this.startingTask();
    if (!firstTask) {
      return null;
    }

    return this.taskVersionById().get(firstTask.resolvedVersionId) ?? null;
  });

  protected readonly firstTaskName = computed(() => {
    const firstTask = this.startingTask();
    if (!firstTask) {
      return null;
    }

    return this.taskDefinitionById().get(firstTask.taskDefinitionId)?.name ?? null;
  });

  protected readonly globalStateFields = computed(() =>
    createTaskPropertyFieldDefinitions(this.firstTaskVersion()?.requiredGlobalState?.properties ?? null),
  );
  protected readonly firstTaskFields = computed(() =>
    createTaskPropertyFieldDefinitions(this.firstTaskVersion()?.inputChannel?.properties ?? null),
  );

  protected readonly runtimeOverrideNodes = computed<RuntimeOverrideNode[]>(() => {
    const blueprintVersion = this.selectedBlueprintVersion();
    if (!blueprintVersion) {
      return [];
    }

    return blueprintVersion.tasks
      .filter((task) => task.instanceId !== blueprintVersion.startingBlueprintNodeId)
      .map((task) => {
        const taskVersion = this.taskVersionById().get(task.resolvedVersionId);
        return {
          instanceId: task.instanceId,
          name: this.taskDefinitionById().get(task.taskDefinitionId)?.name
            ?? `Node ${task.instanceId}`,
          inputProperties: { ...(taskVersion?.inputChannel?.properties ?? {}) },
          propertyCount: Object.keys(taskVersion?.inputChannel?.properties ?? {}).length,
        };
      })
      .filter((node) => node.propertyCount > 0);
  });

  protected readonly overrideCount = computed(() => this.runtimeOverrideNodes().length);
  protected readonly configuredRuntimeOverrideCount = computed(() =>
    Object.values(this.runtimeOverrideStates()).filter((state) => Object.keys(state.overrides).length > 0).length,
  );
  protected readonly runtimeOverridesValid = computed(() =>
    Object.values(this.runtimeOverrideStates()).every((state) => state.valid),
  );

  protected readonly errorMessage = computed(() => {
    if (this.executeErrorMessage()) {
      return this.executeErrorMessage();
    }

    const initialDataState = this.initialDataState();
    if (isErrorRequestState(initialDataState)) {
      return initialDataState.message;
    }

    const selectedBlueprintState = this.selectedBlueprintState();
    if (isErrorRequestState(selectedBlueprintState)) {
      return selectedBlueprintState.message;
    }

    if (this.selectedBlueprintId() && isReadyRequestState(selectedBlueprintState) && !this.firstTaskVersion()) {
      return 'The selected blueprint has no starting node or matching task definition.';
    }

    return null;
  });

  constructor() {
    effect(() => {
      this.resetGroup(this.form.controls.globalState, this.globalStateFields());
      this.resetGroup(this.form.controls.initialTaskInput, this.firstTaskFields());
    });

    effect(() => {
      const currentStates = untracked(() => this.runtimeOverrideStates());
      const nextStates = buildRuntimeOverrideStateMap(this.runtimeOverrideNodes(), currentStates);
      if (!runtimeOverrideStateMapsEqual(currentStates, nextStates)) {
        this.runtimeOverrideStates.set(nextStates);
      }
    });
  }

  protected async execute(): Promise<void> {
    this.successMessage.set(null);
    this.executeErrorMessage.set(null);

    if (this.form.invalid || !this.form.controls.blueprintId.value) {
      this.form.markAllAsTouched();
      return;
    }

    if (!this.runtimeOverridesValid()) {
      this.executeErrorMessage.set('Complete or clear any runtime overrides before starting the process.');
      return;
    }

    this.executing.set(true);
    try {
      const response = await firstValueFrom(this.blueprintService.startProcess(
        this.form.controls.blueprintId.value,
        {
          globalState: this.form.controls.globalState.getRawValue(),
          initialTaskInput: this.form.controls.initialTaskInput.getRawValue(),
          nodeOverrides: this.collectRuntimeOverrides(),
        },
      ));

      this.successMessage.set(
        `Process accepted with id ${response.processId}.`,
      );
      await this.router.navigate(['/processes'], {
        queryParams: {
          blueprintId: this.form.controls.blueprintId.value,
          processId: response.processId,
        },
      });
    } catch {
      this.executeErrorMessage.set('Unable to start the selected process.');
    } finally {
      this.executing.set(false);
    }
  }

  protected toggleAdvancedOverrides(): void {
    this.advancedOverridesExpanded.update((expanded) => !expanded);
  }

  protected setAdvancedOverridesExpanded(expanded: boolean): void {
    this.advancedOverridesExpanded.set(expanded);
  }

  protected runtimeOverrideResetKey(node: RuntimeOverrideNode): string {
    return `${this.selectedBlueprintVersion()?.blueprintVersionId ?? 'none'}:${node.instanceId}`;
  }

  protected runtimeOverrideInitialOverrides(nodeId: string): Record<string, unknown> {
    return this.runtimeOverrideStates()[nodeId]?.overrides ?? {};
  }

  protected onRuntimeOverrideStateChanged(state: PropertyOverrideEditorState): void {
    this.runtimeOverrideStates.update((current) => ({
      ...current,
      [state.editorId]: state,
    }));
  }

  protected resetConfiguration(): void {
    this.form.reset({
      blueprintId: null,
    });
    this.runtimeOverrideStates.set({});
    this.successMessage.set(null);
    this.executeErrorMessage.set(null);
    this.advancedOverridesExpanded.set(false);
  }

  private resetGroup(group: FormGroup, fields: DynamicFieldDefinition[]): void {
    for (const controlName of Object.keys(group.controls)) {
      group.removeControl(controlName);
    }

    for (const field of fields) {
      group.addControl(field.key, createTaskPropertyControl(field));
    }
  }

  private collectRuntimeOverrides(): Record<string, Record<string, unknown>> {
    return Object.fromEntries(
      Object.entries(this.runtimeOverrideStates())
        .filter(([, state]) => Object.keys(state.overrides).length > 0)
        .map(([nodeId, state]) => [nodeId, state.overrides]),
    );
  }
}
