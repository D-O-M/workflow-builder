import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { catchError, distinctUntilChanged, firstValueFrom, forkJoin, map, of, startWith } from 'rxjs';
import { BlueprintEditorService, BlueprintViewModel, BlueprintVersionViewModel } from '../domain/blueprint-editor.service';
import { DynamicFieldDefinition, DynamicFormFieldComponent } from '../components/dynamic-form-field.component';
import { ITaskChannel, ITaskDefinition, ITaskVersion } from '../interfaces/ITaskDefinition';
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

interface WorkspaceLink {
  icon: string;
  label: string;
}

interface OverridePreview {
  title: string;
  subtitle: string;
  values: Array<{ label: string; value: string }>;
}

interface ExecuteFloInitialData {
  blueprints: BlueprintViewModel[];
  taskDefinitions: ITaskDefinition[];
}

const WORKSPACE_LINKS: WorkspaceLink[] = [
  { icon: 'architecture', label: 'My Blueprints' },
  { icon: 'extension', label: 'Plugin Library' },
  { icon: 'inventory_2', label: 'Shared Assets' },
  { icon: 'terminal', label: 'System Logs' },
];

@Component({
  selector: 'app-execute-flo-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButtonModule,
    MatIconModule,
    DynamicFormFieldComponent,
  ],
  templateUrl: './execute-flo-page.component.html',
  styleUrl: './execute-flo-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExecuteFloPageComponent {
  private readonly fb = inject(FormBuilder);
  private readonly blueprintService = inject(BlueprintEditorService);
  private readonly taskDefinitionsService = inject(TaskDefinitionsService);
  private readonly router = inject(Router);

  protected readonly executing = signal(false);
  protected readonly executeErrorMessage = signal<string | null>(null);
  protected readonly successMessage = signal<string | null>(null);
  protected readonly workspaceLinks = WORKSPACE_LINKS;

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
      map((data) => readyRequestState<ExecuteFloInitialData>(data)),
      startWith(loadingRequestState<ExecuteFloInitialData>()),
      catchError(() => of(errorRequestState<ExecuteFloInitialData>('Unable to load Flos and task definitions.'))),
    ),
    { initialValue: loadingRequestState<ExecuteFloInitialData>() },
  );

  private readonly selectedBlueprintId = toSignal(
    this.form.controls.blueprintId.valueChanges.pipe(startWith(this.form.controls.blueprintId.value)),
    { initialValue: this.form.controls.blueprintId.value },
  );

  private readonly selectedBlueprintState = toSignal(
    createRequestStateStream(
      toObservable(this.selectedBlueprintId).pipe(distinctUntilChanged()),
      (blueprintId) => this.blueprintService.getBlueprintById(blueprintId),
      'Unable to load the selected Flo.',
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

  protected readonly firstTaskVersion = computed<ITaskVersion | null>(() => {
    const blueprintVersion = this.selectedBlueprintVersion();
    if (!blueprintVersion) {
      return null;
    }

    const firstTask = blueprintVersion.tasks.find(
      (task) => task.instanceId === blueprintVersion.startingTaskInstanceId,
    );

    if (!firstTask) {
      return null;
    }

    return this.taskDefinitions()
      .flatMap((definition) => definition.versions)
      .find((version) => version.id === firstTask.resolvedVersionId) ?? null;
  });

  protected readonly firstTaskName = computed(() => {
    const taskVersion = this.firstTaskVersion();
    if (!taskVersion) {
      return null;
    }

    return this.taskDefinitions().find((definition) =>
      definition.versions.some((version) => version.id === taskVersion.id),
    )?.name ?? null;
  });

  protected readonly globalStateFields = computed(() =>
    this.createFieldDefinitions(this.firstTaskVersion()?.requiredGlobalState ?? null),
  );
  protected readonly firstTaskFields = computed(() =>
    this.createFieldDefinitions(this.firstTaskVersion()?.inputChannel ?? null),
  );

  protected readonly overridePreviews = computed<OverridePreview[]>(() => {
    const firstTask = this.firstTaskName() ?? 'Primary Node';
    const taskValues = this.firstTaskFields().slice(0, 2).map((field, index) => ({
      label: field.label,
      value: this.previewValue(field.label, index === 0 ? 'Configured' : 'Inherited'),
    }));
    const globalValues = this.globalStateFields().slice(0, 2).map((field, index) => ({
      label: field.label,
      value: this.previewValue(field.label, index === 0 ? 'Resolved' : 'Default'),
    }));

    return [
      {
        title: 'INSTANCE A-1',
        subtitle: `${firstTask}: Primary Filter`,
        values: taskValues.length > 0
          ? taskValues
          : [
            { label: 'Entrypoint Source', value: 'Configured at runtime' },
            { label: 'Retry Limit', value: '3' },
          ],
      },
      {
        title: 'INSTANCE B-2',
        subtitle: `${firstTask}: Transformation`,
        values: globalValues.length > 0
          ? globalValues
          : [
            { label: 'Execution Environment', value: 'Production Cluster' },
            { label: 'Notification Webhook', value: 'Configured securely' },
          ],
      },
    ];
  });

  protected readonly overrideCount = computed(() =>
    this.firstTaskFields().length + this.globalStateFields().length,
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
      return 'The selected Flo has no starting task or matching task definition.';
    }

    return null;
  });

  constructor() {
    effect(() => {
      this.resetGroup(this.form.controls.globalState, this.globalStateFields());
      this.resetGroup(this.form.controls.initialTaskInput, this.firstTaskFields());
    });
  }

  protected async execute(): Promise<void> {
    this.successMessage.set(null);
    this.executeErrorMessage.set(null);

    if (this.form.invalid || !this.form.controls.blueprintId.value) {
      this.form.markAllAsTouched();
      return;
    }

    this.executing.set(true);
    try {
      const response = await firstValueFrom(this.blueprintService.executeBlueprint(
        this.form.controls.blueprintId.value,
        {
          globalState: this.form.controls.globalState.getRawValue(),
          initialTaskInput: this.form.controls.initialTaskInput.getRawValue(),
        },
      ));

      this.successMessage.set(
        `Execution accepted for workflow instance ${response.workflowInstanceId}.`,
      );
      await this.router.navigate(['/executions'], {
        queryParams: {
          blueprintId: this.form.controls.blueprintId.value,
          workflowInstanceId: response.workflowInstanceId,
        },
      });
    } catch {
      this.executeErrorMessage.set('Unable to execute the selected Flo.');
    } finally {
      this.executing.set(false);
    }
  }

  private createFieldDefinitions(channel: ITaskChannel | null): DynamicFieldDefinition[] {
    if (!channel) {
      return [];
    }

    return Object.entries(channel.properties).map(([key, type]) => ({
      key,
      label: this.toLabel(key),
      kind: this.toFieldKind(type),
      required: type !== 'Boolean',
    }));
  }

  private resetGroup(group: FormGroup, fields: DynamicFieldDefinition[]): void {
    for (const controlName of Object.keys(group.controls)) {
      group.removeControl(controlName);
    }

    for (const field of fields) {
      group.addControl(field.key, this.createControl(field));
    }
  }

  private createControl(field: DynamicFieldDefinition): AbstractControl {
    switch (field.kind) {
      case 'boolean':
        return new FormControl(false, { nonNullable: true });
      case 'number':
        return new FormControl<number | null>(null, field.required ? [Validators.required] : []);
      case 'guid':
        return new FormControl('', {
          nonNullable: true,
          validators: [
            ...(field.required ? [Validators.required] : []),
            Validators.pattern(/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$/),
          ],
        });
      default:
        return new FormControl('', {
          nonNullable: true,
          validators: field.required ? [Validators.required] : [],
        });
    }
  }

  private toFieldKind(type: 'Number' | 'String' | 'Boolean' | 'Guid'): DynamicFieldDefinition['kind'] {
    switch (type) {
      case 'Number':
        return 'number';
      case 'Boolean':
        return 'boolean';
      case 'Guid':
        return 'guid';
      default:
        return 'string';
    }
  }

  private toLabel(key: string): string {
    return key
      .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
      .replace(/[_-]+/g, ' ')
      .replace(/^./, (value) => value.toUpperCase());
  }

  private previewValue(label: string, fallback: string): string {
    const normalized = label.toLocaleLowerCase();
    if (normalized.includes('timeout')) return '30000';
    if (normalized.includes('memory')) return '2048MB';
    if (normalized.includes('concurrency')) return '8';
    if (normalized.includes('cpu')) return '512';
    return fallback;
  }
}
