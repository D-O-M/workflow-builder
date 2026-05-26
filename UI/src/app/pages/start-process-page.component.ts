import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import {
  MatAutocompleteModule,
  MatAutocompleteSelectedEvent,
  MatAutocompleteTrigger,
} from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatStepperModule } from '@angular/material/stepper';
import { catchError, distinctUntilChanged, firstValueFrom, forkJoin, map, of, startWith } from 'rxjs';
import { BlueprintEditorService, BlueprintViewModel, BlueprintVersionViewModel, PhasedBlueprintViewModel } from '../domain/blueprint-editor.service';
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
import {
  createTaskPropertyControl,
  createTaskPropertyFieldDefinitions,
  serializeTaskPropertyFormValues,
} from '../domain/task-property-form-utils';
import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

interface RuntimeOverrideNode {
  instanceId: string;
  name: string;
  inputProperties: ITaskPropertyDefinition[];
  propertyCount: number;
}

interface StartProcessInitialData {
  blueprints: BlueprintViewModel[];
  phasedBlueprints: PhasedBlueprintViewModel[];
  taskDefinitions: ITaskDefinition[];
}

interface BlueprintPickerSuggestion {
  id: string;
  label: string;
  support: string;
  kind: 'blueprint' | 'phaseSeries';
  definitionId: number;
  firstPhaseBlueprintId: number;
}

interface BlueprintPickerSuggestionViewModel extends BlueprintPickerSuggestion {
  isActive: boolean;
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

function getBlueprintMatch(
  blueprintName: string,
  query: string,
): { rank: number; score: number } | null {
  const normalizedName = blueprintName.trim().toLocaleLowerCase();
  const normalizedQuery = query.trim().toLocaleLowerCase();

  if (normalizedName === normalizedQuery) {
    return { rank: 0, score: 0 };
  }

  if (normalizedName.startsWith(normalizedQuery)) {
    return { rank: 1, score: normalizedName.length - normalizedQuery.length };
  }

  const containsIndex = normalizedName.indexOf(normalizedQuery);
  if (containsIndex >= 0) {
    return { rank: 2, score: containsIndex };
  }

  const fuzzyScore = getFuzzyMatchScore(normalizedName, normalizedQuery);
  if (fuzzyScore !== null) {
    return { rank: 3, score: fuzzyScore };
  }

  return null;
}

function getFuzzyMatchScore(text: string, query: string): number | null {
  let currentIndex = 0;
  let previousMatchIndex = -1;
  let gapScore = 0;

  for (const character of query) {
    const matchIndex = text.indexOf(character, currentIndex);
    if (matchIndex < 0) {
      return null;
    }

    if (previousMatchIndex >= 0) {
      gapScore += matchIndex - previousMatchIndex - 1;
    }

    previousMatchIndex = matchIndex;
    currentIndex = matchIndex + 1;
  }

  return gapScore + (text.length - query.length);
}

function filterBlueprintSuggestions<T extends { label: string }>(
  blueprints: T[],
  query: string,
): T[] {
  const trimmedQuery = query.trim();
  if (!trimmedQuery) {
    return [...blueprints].sort((a, b) => a.label.localeCompare(b.label));
  }

  return blueprints
    .map((blueprint) => ({
      blueprint,
      match: getBlueprintMatch(blueprint.label, trimmedQuery),
    }))
    .filter((item): item is { blueprint: T; match: { rank: number; score: number } } =>
      item.match !== null)
    .sort((a, b) =>
      a.match.rank - b.match.rank
      || a.match.score - b.match.score
      || a.blueprint.label.localeCompare(b.blueprint.label))
    .map((item) => item.blueprint);
}

@Component({
  selector: 'app-start-process-page',
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatCardModule,
    MatChipsModule,
    MatExpansionModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatStepperModule,
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
  protected readonly runtimeOverrideStates = signal<Record<string, PropertyOverrideEditorState>>({});
  protected readonly pickerMode = signal<'search' | 'selected'>('search');
  protected readonly pickerPanelOpen = signal(false);
  protected readonly wizardStepIndex = signal(0);
  private readonly pickerSelectionInProgress = signal(false);
  private readonly pickerFocusRequested = signal(false);
  private readonly blueprintPickerInput = viewChild<ElementRef<HTMLInputElement>>('blueprintPickerInput');
  private readonly blueprintPickerTrigger = viewChild(MatAutocompleteTrigger);
  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
  private readonly blueprintSearchQuery = toSignal(this.blueprintSearch.valueChanges, {
    initialValue: this.blueprintSearch.value,
  });

  protected readonly form = this.fb.group({
    blueprintId: this.fb.control<number | null>(null, Validators.required),
    initialTaskInput: this.fb.group({}),
  });

  private readonly initialDataState = toSignal(
    forkJoin({
      blueprints: this.blueprintService.getBlueprints(),
      phasedBlueprints: this.blueprintService.getPhasedBlueprints(),
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
  private readonly initialTaskInputStatus = toSignal(
    this.form.controls.initialTaskInput.statusChanges.pipe(startWith(this.form.controls.initialTaskInput.status)),
    { initialValue: this.form.controls.initialTaskInput.status },
  );
  private readonly formStatus = toSignal(
    this.form.statusChanges.pipe(startWith(this.form.status)),
    { initialValue: this.form.status },
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
  protected readonly phasedBlueprints = computed<PhasedBlueprintViewModel[]>(() => {
    const state = this.initialDataState();
    return isReadyRequestState(state) ? state.data.phasedBlueprints : [];
  });
  protected readonly taskDefinitions = computed<ITaskDefinition[]>(() => {
    const state = this.initialDataState();
    return isReadyRequestState(state) ? state.data.taskDefinitions : [];
  });
  protected readonly selectedBlueprint = computed<BlueprintViewModel | null>(() => {
    const state = this.selectedBlueprintState();
    return isReadyRequestState(state) ? state.data : null;
  });
  protected readonly selectedBlueprintSummary = computed<BlueprintViewModel | null>(() =>
    this.blueprints().find((blueprint) => blueprint.id === this.selectedBlueprintId()) ?? null,
  );
  protected readonly selectedPhaseSeriesId = signal<number | null>(null);
  protected readonly selectedPhaseSeriesSummary = computed<PhasedBlueprintViewModel | null>(() =>
    this.phasedBlueprints().find((phaseSeries) => phaseSeries.id === this.selectedPhaseSeriesId()) ?? null,
  );
  protected readonly selectedBlueprintVersion = computed<BlueprintVersionViewModel | null>(() =>
    this.selectedBlueprint()?.versions[0] ?? null,
  );
  protected readonly pickerDisplayName = computed(() =>
    this.selectedPhaseSeriesSummary()?.name ?? this.selectedBlueprintSummary()?.name ?? 'Select a blueprint',
  );
  private readonly pickerTargets = computed<BlueprintPickerSuggestion[]>(() => [
    ...this.blueprints().map((blueprint) => ({
      id: `blueprint:${blueprint.id}`,
      label: blueprint.name,
      support: 'Blueprint',
      kind: 'blueprint' as const,
      definitionId: blueprint.id,
      firstPhaseBlueprintId: blueprint.id,
    })),
    ...this.phasedBlueprints().flatMap((phaseSeries) => {
      const version = this.latestPhasedVersion(phaseSeries);
      const firstPhase = version?.phases.find((phase) => phase.phaseIndex === 0) ?? null;
      return firstPhase
        ? [{
            id: `phaseSeries:${phaseSeries.id}`,
            label: phaseSeries.name,
            support: `${version?.phases.length ?? 0} phase series`,
            kind: 'phaseSeries' as const,
            definitionId: phaseSeries.id,
            firstPhaseBlueprintId: firstPhase.blueprintDefinitionId,
          }]
        : [];
    }),
  ]);

  protected readonly pickerSuggestions = computed<BlueprintPickerSuggestionViewModel[]>(() => {
    const query = this.blueprintSearchQuery();
    const filteredTargets = filterBlueprintSuggestions(this.pickerTargets(), query);
    const activeBlueprintId = this.selectedBlueprintId();
    const activePhaseSeriesId = this.selectedPhaseSeriesId();
    if (activeBlueprintId === null && activePhaseSeriesId === null) {
      return filteredTargets.map((target) => ({
        ...target,
        isActive: false,
      }));
    }

    const activeId = activePhaseSeriesId !== null ? `phaseSeries:${activePhaseSeriesId}` : `blueprint:${activeBlueprintId}`;
    const activeTarget = filteredTargets.find((target) => target.id === activeId);
    const orderedTargets = activeTarget
      ? [activeTarget, ...filteredTargets.filter((target) => target.id !== activeId)]
      : filteredTargets;

    return orderedTargets.map((target) => ({
      ...target,
      isActive: target.id === activeId,
    }));
  });

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

    return firstTask.nodeKind === 'CollectionIterator'
      ? 'Collection Iterator'
      : this.taskDefinitionById().get(firstTask.taskDefinitionId ?? 0)?.name ?? null;
  });

  protected readonly firstTaskFields = computed(() =>
    createTaskPropertyFieldDefinitions(this.firstTaskVersion()?.inputChannel?.properties ?? null),
  );
  protected readonly selectedBlueprintConfigured = computed(() => this.selectedBlueprintId() !== null);
  protected readonly canAdvanceFromInitialisation = computed(() =>
    this.selectedBlueprintConfigured()
    && !this.blueprintLoading()
    && this.initialTaskInputStatus() === 'VALID',
  );
  protected readonly canStartProcess = computed(() =>
    !this.executing()
    && !this.blueprintLoading()
    && !this.loading()
    && this.formStatus() === 'VALID'
    && this.runtimeOverridesValid(),
  );
  protected readonly startButtonLabel = computed(() =>
    this.selectedPhaseSeriesId() === null ? 'Start Process' : 'Start Phase Series');

  protected readonly runtimeOverrideNodes = computed<RuntimeOverrideNode[]>(() => {
    const blueprintVersion = this.selectedBlueprintVersion();
    if (!blueprintVersion) {
      return [];
    }

    return blueprintVersion.tasks
      .filter((task) => task.instanceId !== blueprintVersion.startingBlueprintNodeId)
      .map((task) => {
        if (task.nodeKind === 'CollectionIterator') {
          return {
            instanceId: task.instanceId,
            name: 'Collection Iterator',
            inputProperties: [],
            propertyCount: 0,
          };
        }

        const taskVersion = this.taskVersionById().get(task.resolvedVersionId);
        return {
          instanceId: task.instanceId,
          name: this.taskDefinitionById().get(task.taskDefinitionId ?? 0)?.name
            ?? `Node ${task.instanceId}`,
          inputProperties: [...(taskVersion?.inputChannel?.properties ?? [])],
          propertyCount: (taskVersion?.inputChannel?.properties ?? []).length,
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
      this.resetGroup(this.form.controls.initialTaskInput, this.firstTaskFields());
    });

    effect(() => {
      const currentStates = untracked(() => this.runtimeOverrideStates());
      const nextStates = buildRuntimeOverrideStateMap(this.runtimeOverrideNodes(), currentStates);
      if (!runtimeOverrideStateMapsEqual(currentStates, nextStates)) {
        this.runtimeOverrideStates.set(nextStates);
      }
    });

    effect(() => {
      const selectedBlueprintId = this.selectedBlueprintId();
      if (this.pickerMode() === 'search') {
        return;
      }

      this.pickerMode.set(selectedBlueprintId === null ? 'search' : 'selected');
    });

    effect(() => {
      if (this.pickerMode() !== 'search' || !this.pickerFocusRequested()) {
        return;
      }

      this.scheduleUiTask(() => {
        this.blueprintPickerInput()?.nativeElement.focus();
        this.blueprintPickerTrigger()?.openPanel();
        this.pickerFocusRequested.set(false);
      });
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
      const processPayload = {
        initialTaskInput: this.serializeFieldGroup(this.firstTaskFields(), this.form.controls.initialTaskInput),
        nodeOverrides: this.collectRuntimeOverrides(),
      };
      const phaseSeriesId = this.selectedPhaseSeriesId();
      let processId: string;
      if (phaseSeriesId !== null) {
        const phasedResponse = await firstValueFrom(this.blueprintService.startPhasedProcess(
          phaseSeriesId,
          processPayload,
        ));
        processId = phasedResponse.childProcessId;
        this.successMessage.set(`Phase series accepted with id ${phasedResponse.phasedProcessId}.`);
      } else {
        const response = await firstValueFrom(this.blueprintService.startProcess(
          this.form.controls.blueprintId.value,
          processPayload,
        ));
        processId = response.processId;
        this.successMessage.set(`Process accepted with id ${processId}.`);
      }

      await this.router.navigate(['/processes'], {
        queryParams: {
          blueprintId: this.form.controls.blueprintId.value,
          processId,
        },
      });
    } catch {
      this.executeErrorMessage.set('Unable to start the selected process.');
    } finally {
      this.executing.set(false);
    }
  }

  protected runtimeOverrideResetKey(node: RuntimeOverrideNode): string {
    return `${this.selectedBlueprintVersion()?.blueprintVersionId ?? 'none'}:${node.instanceId}`;
  }

  protected runtimeOverrideInitialOverrides(nodeId: string): Record<string, unknown> {
    return this.runtimeOverrideStates()[nodeId]?.overrides ?? {};
  }

  protected runtimeOverrideConfiguredCount(nodeId: string): number {
    return Object.keys(this.runtimeOverrideStates()[nodeId]?.overrides ?? {}).length;
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
    this.blueprintSearch.setValue('');
    this.selectedPhaseSeriesId.set(null);
    this.pickerMode.set('search');
    this.pickerPanelOpen.set(false);
    this.pickerSelectionInProgress.set(false);
    this.pickerFocusRequested.set(false);
    this.runtimeOverrideStates.set({});
    this.successMessage.set(null);
    this.executeErrorMessage.set(null);
    this.wizardStepIndex.set(0);
  }

  protected onWizardStepIndexChanged(index: number): void {
    this.wizardStepIndex.set(index);
  }

  protected goToNextWizardStep(): void {
    this.wizardStepIndex.update((currentStep) => Math.min(currentStep + 1, 1));
  }

  protected goToPreviousWizardStep(): void {
    this.wizardStepIndex.update((currentStep) => Math.max(currentStep - 1, 0));
  }

  protected beginBlueprintSearch(): void {
    this.blueprintSearch.setValue('');
    this.pickerFocusRequested.set(true);
    this.pickerMode.set('search');
  }

  protected onBlueprintSearchFocus(): void {
    if (this.pickerMode() !== 'search') {
      return;
    }

    this.blueprintPickerTrigger()?.openPanel();
  }

  protected onBlueprintSearchKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape') {
      return;
    }

    event.preventDefault();
    this.restorePickerAfterSearch();
  }

  protected onBlueprintAutocompleteOpened(): void {
    this.pickerPanelOpen.set(true);
  }

  protected onBlueprintAutocompleteClosed(): void {
    this.pickerPanelOpen.set(false);

    if (this.pickerSelectionInProgress()) {
      this.pickerSelectionInProgress.set(false);
      return;
    }

    if (this.pickerMode() !== 'search') {
      return;
    }

    this.restorePickerAfterSearch();
  }

  protected onBlueprintSuggestionSelected(event: MatAutocompleteSelectedEvent): void {
    const value = String(event.option.value);
    const [kind, idText] = value.split(':');
    const definitionId = Number(idText);
    if ((kind !== 'blueprint' && kind !== 'phaseSeries') || Number.isNaN(definitionId)) {
      return;
    }

    const suggestion = this.pickerTargets().find((candidate) => candidate.id === value) ?? null;
    if (!suggestion) {
      return;
    }

    this.pickerSelectionInProgress.set(true);
    this.selectedPhaseSeriesId.set(kind === 'phaseSeries' ? definitionId : null);
    this.form.controls.blueprintId.setValue(suggestion.firstPhaseBlueprintId);
    this.blueprintSearch.setValue('');
    this.pickerMode.set('selected');
    this.wizardStepIndex.set(0);
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

  private serializeFieldGroup(
    fields: DynamicFieldDefinition[],
    group: FormGroup,
  ): Record<string, unknown> {
    const rawValues = group.getRawValue();
    return serializeTaskPropertyFormValues(fields, rawValues);
  }

  private restorePickerAfterSearch(): void {
    this.pickerFocusRequested.set(false);
    this.blueprintSearch.setValue('');
    this.pickerMode.set(this.selectedBlueprintId() === null ? 'search' : 'selected');
  }

  private latestPhasedVersion(series: PhasedBlueprintViewModel) {
    return [...series.versions].sort((left, right) => right.version - left.version)[0] ?? null;
  }

  private scheduleUiTask(task: () => void): void {
    if (typeof requestAnimationFrame === 'function') {
      requestAnimationFrame(task);
      return;
    }

    queueMicrotask(task);
  }
}
