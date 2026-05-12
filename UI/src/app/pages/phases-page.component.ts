import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  OnDestroy,
  computed,
  inject,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent, MatAutocompleteTrigger } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { firstValueFrom, forkJoin } from 'rxjs';
import { BlueprintCanvasComponent } from '../components/blueprint-canvas.component';
import {
  BlueprintCanvasEvent,
  BlueprintCanvasGraph,
  BlueprintCanvasRuntime,
  BlueprintCanvasSelection,
  CanvasInputPropertyHit,
  CanvasOutputPropertyDragEvent,
  CanvasViewportInsets,
} from '../components/blueprint-canvas-contract';
import {
  BlueprintEditorService,
  BlueprintViewModel,
  BlueprintVersionViewModel,
  PhasedBlueprintInputModel,
  PhasedBlueprintPhaseInputModel,
  PhasedBlueprintTransitionViewModel,
  PhasedBlueprintVersionInputModel,
  PhasedBlueprintVersionViewModel,
  PhasedBlueprintViewModel,
} from '../domain/blueprint-editor.service';
import { ConnectionModel } from '../domain/connection-model';
import { NodeModel } from '../domain/node-model';
import { PhaseSplitMode, normalizeBridgePosition as normalizePhaseBridgePosition } from '../domain/phase-bridge-geometry';
import { PhaseBridgeController } from '../domain/phase-bridge-controller';
import { PhaseBridgeCurveFactory } from '../domain/phase-bridge-curve-factory';
import { PhaseCanvasPairPresenter } from '../domain/phase-canvas-pair-presenter';
import {
  DataBridgeMappingDraft,
  PhaseSeriesCanvasModel,
  PhaseSeriesPhaseCanvas,
  PhaseSeriesTransitionCanvas,
  clearDataBridgeMappingTarget,
  completeDataBridgeMappingTarget,
  createDataBridgeMappingFromHandoff,
  createPhasePairOptions,
  createTransitionDraft,
  reconcileTransitionsForPhaseVersionChange,
  removeDataBridgeMapping,
  serializeTransitionMappings,
  upsertDataBridgeSourceMapping,
  validatePhaseSeries,
} from '../domain/phase-series-canvas';
import { TaskDefinitionsService } from '../domain/task-definitions.service';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';
import {
  PhaseBridgeCurve,
  PhaseBridgeDragState,
  PhaseBridgeGuidance,
  PhaseBridgeSelection,
  PhaseWorkbenchDomRect,
} from '../domain/phase-workbench-contract';

interface EditablePhaseSeries extends PhaseSeriesCanvasModel {
  id: number | null;
  name: string;
  versionId: number | null;
  versionNumber: number | null;
}

interface PhaseSeriesSummary {
  id: number;
  name: string;
  version: number | null;
  phaseCount: number;
  selectedBlueprints: string;
  bridgeStatus: string;
  updateAvailable: boolean;
}

interface BlueprintVersionOption {
  value: string;
  blueprintId: number;
  versionId: number;
  blueprintName: string;
  versionNumber: number;
  label: string;
}

type BridgeDragState = PhaseBridgeDragState;
type BridgeSelection = PhaseBridgeSelection;
type BridgeCurve = PhaseBridgeCurve;
type BridgeGuidance = PhaseBridgeGuidance;

@Component({
  selector: 'app-phases-page',
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatMenuModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatTooltipModule,
    BlueprintCanvasComponent,
  ],
  templateUrl: './phases-page.component.html',
  styleUrl: './phases-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PhasesPageComponent implements OnDestroy {
  private readonly editor = inject(BlueprintEditorService);
  private readonly taskDefinitions = inject(TaskDefinitionsService);
  private readonly router = inject(Router);
  private readonly phaseCanvases = viewChildren(BlueprintCanvasComponent);
  private readonly workbench = viewChild<ElementRef<HTMLElement>>('workbench');
  private readonly bridgeDivider = viewChild<ElementRef<HTMLElement>>('bridgeDivider');
  private readonly seriesPickerInput = viewChild<ElementRef<HTMLInputElement>>('seriesPickerInput');
  private readonly seriesPickerTrigger = viewChild(MatAutocompleteTrigger);
  private readonly canvasPairPresenter = new PhaseCanvasPairPresenter();
  private readonly bridgeCurveFactory = new PhaseBridgeCurveFactory();
  private readonly bridgeController = new PhaseBridgeController(() => this.scheduleBridgeLineRefresh());
  private bridgeLineFrameId: number | null = null;

  protected readonly seriesSearch = new FormControl('', { nonNullable: true });
  private readonly seriesQuery = toSignal(this.seriesSearch.valueChanges, { initialValue: '' });
  protected readonly addPhaseSelection = signal<string | null>(null);

  protected readonly loading = signal(false);
  protected readonly openingSeriesId = signal<number | null>(null);
  protected readonly saving = signal(false);
  protected readonly phasePanelOpen = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly successMessage = signal<string | null>(null);
  protected readonly bridgeDropMessage = this.bridgeController.dropMessage;
  protected readonly bridgeDrag = this.bridgeController.drag;
  protected readonly bridgePreviewPosition = this.bridgeController.previewPosition;
  protected readonly selectedBridgeConnection = this.bridgeController.selection;
  protected readonly bridgeCurves = signal<BridgeCurve[]>([]);
  protected readonly bridgeDragCurve = signal<string | null>(null);
  protected readonly blueprints = signal<BlueprintViewModel[]>([]);
  protected readonly phasedBlueprints = signal<PhasedBlueprintViewModel[]>([]);
  protected readonly tasks = signal<ITaskDefinition[]>([]);
  protected readonly selectedSeries = signal<EditablePhaseSeries | null>(null);
  protected readonly activePairIndex = signal(0);
  protected readonly phaseCanvasRevision = signal(0);
  protected readonly splitMode = signal<PhaseSplitMode>('horizontal');

  protected readonly sourceCanvasViewportInsets = computed<CanvasViewportInsets>(() => ({
    top: 42,
    right: 78,
    bottom: 42,
    left: 32,
  }));
  protected readonly destinationCanvasViewportInsets = computed<CanvasViewportInsets>(() => ({
    top: 42,
    right: 78,
    bottom: 42,
    left: 32,
  }));
  protected readonly sourceCanvasRuntime = computed<BlueprintCanvasRuntime>(() =>
    this.canvasPairPresenter.sourceRuntime(this.sourceCanvasViewportInsets()));
  protected readonly destinationCanvasRuntime = computed<BlueprintCanvasRuntime>(() =>
    this.canvasPairPresenter.destinationRuntime(this.destinationCanvasViewportInsets()));
  protected readonly phaseCanvasSelection = computed<BlueprintCanvasSelection>(() =>
    this.canvasPairPresenter.selection());

  protected readonly blueprintVersionOptions = computed<BlueprintVersionOption[]>(() =>
    [...this.blueprints()]
      .sort((left, right) => left.name.localeCompare(right.name))
      .flatMap((blueprint) =>
        this.sortBlueprintVersions(blueprint.versions).map((version) => ({
          value: this.blueprintVersionValue(blueprint.id, version.blueprintVersionId),
          blueprintId: blueprint.id,
          versionId: version.blueprintVersionId,
          blueprintName: blueprint.name,
          versionNumber: version.version,
          label: `${blueprint.name} v${version.version}`,
        })),
      ),
  );

  protected readonly seriesSummaries = computed<PhaseSeriesSummary[]>(() =>
    [...this.phasedBlueprints()]
      .sort((left, right) => left.name.localeCompare(right.name))
      .map((series) => this.createSeriesSummary(series)),
  );

  protected readonly seriesPickerOptions = computed(() => {
    const query = this.seriesQuery().trim().toLowerCase();
    return this.seriesSummaries().filter((summary) =>
      !query ||
      summary.name.toLowerCase().includes(query) ||
      summary.selectedBlueprints.toLowerCase().includes(query));
  });

  protected readonly selectedSeriesPickerLabel = computed(() =>
    this.selectedSeries()?.name ?? 'Search phase series',
  );

  protected readonly phasePairOptions = computed(() =>
    this.selectedSeries()
      ? createPhasePairOptions(this.selectedSeries()!.phases)
      : [],
  );

  protected readonly activeTransition = computed<PhaseSeriesTransitionCanvas | null>(() => {
    const series = this.selectedSeries();
    if (!series) {
      return null;
    }

    const sourcePhase = series.phases[this.activePairIndex()];
    const destinationPhase = series.phases[this.activePairIndex() + 1];
    if (!sourcePhase || !destinationPhase) {
      return null;
    }

    return series.transitions.find((transition) =>
      transition.sourcePhaseId === sourcePhase.phaseId &&
      transition.destinationPhaseId === destinationPhase.phaseId) ?? null;
  });

  protected readonly activeSourcePhase = computed(() =>
    this.selectedSeries()?.phases[this.activePairIndex()] ?? null,
  );

  protected readonly activeDestinationPhase = computed(() =>
    this.selectedSeries()?.phases[this.activePairIndex() + 1] ?? null,
  );

  protected readonly activeTransitionIssues = computed(() => {
    const series = this.selectedSeries();
    if (!series) {
      return [];
    }

    const transitionIndex = this.activeTransitionIndex(series);
    return validatePhaseSeries(series).filter((issue) => issue.transitionIndex === transitionIndex);
  });

  protected readonly validationMessages = computed(() => {
    const series = this.selectedSeries();
    if (!series) {
      return [];
    }

    const messages = validatePhaseSeries(series).map((issue) => issue.message);
    if (!series.name.trim()) {
      messages.unshift('Series name is required.');
    }

    return messages;
  });

  protected readonly canSaveSeries = computed(() => {
    const series = this.selectedSeries();
    return !!series &&
      !this.saving() &&
      series.name.trim().length > 0 &&
      series.phases.length >= 2 &&
      series.phases.length <= 4 &&
      validatePhaseSeries(series).length === 0;
  });

  protected readonly bridgeMappingCount = computed(() =>
    this.activeTransition()?.mappings.length ?? 0,
  );

  protected readonly incompleteBridgeMappingCount = computed(() =>
    this.activeTransition()?.mappings.filter((mapping) => !mapping.targetProperty).length ?? 0,
  );

  protected readonly bridgeMappingCountLabel = computed(() =>
    this.bridgeController.mappingCountLabel(this.bridgeMappingCount(), this.incompleteBridgeMappingCount()));

  protected readonly bridgeGuidance = computed<BridgeGuidance>(() =>
    this.bridgeController.guidance(this.bridgeMappingCount(), this.incompleteBridgeMappingCount()));

  protected readonly bridgeGuidanceLabel = computed(() =>
    this.bridgeController.guidanceLabel(this.bridgeMappingCount(), this.incompleteBridgeMappingCount()));

  protected readonly phaseSetupRequired = computed(() => {
    const series = this.selectedSeries();
    return !!series && series.phases.length < 2;
  });

  protected readonly showTopNewSeriesButton = computed(() => this.selectedSeries() !== null);

  constructor() {
    void this.refreshAll();
  }

  @HostListener('window:keydown', ['$event'])
  protected onWindowKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.bridgeDrag() !== null) {
      event.preventDefault();
      this.cancelBridgeDrag();
      return;
    }

    if (!this.selectedBridgeConnection() || this.isEditableTarget(event.target)) {
      return;
    }

    if (event.key !== 'Delete' && event.key !== 'Backspace') {
      return;
    }

    event.preventDefault();
    this.deleteSelectedBridgeConnection();
  }

  @HostListener('window:pointerup', ['$event'])
  protected onWindowPointerUp(event: PointerEvent): void {
    if (this.bridgeDrag()?.kind === 'source-output') {
      this.finishSourceBridgeDrag(event.clientX, event.clientY, false);
    }
  }

  @HostListener('window:pointercancel', ['$event'])
  protected onWindowPointerCancel(_event: PointerEvent): void {
    if (this.bridgeDrag()?.kind === 'source-output') {
      this.cancelBridgeDrag();
    }
  }

  ngOnDestroy(): void {
    this.stopBridgeOutputDragListeners();
    if (this.bridgeLineFrameId !== null) {
      this.cancelBridgeLineFrame(this.bridgeLineFrameId);
      this.bridgeLineFrameId = null;
    }
  }

  protected createSeries(): void {
    this.errorMessage.set(null);
    this.successMessage.set(null);
    this.bridgeDropMessage.set(null);
    this.addPhaseSelection.set(null);
    this.selectedSeries.set({
      id: null,
      name: 'Untitled phase series',
      versionId: null,
      versionNumber: null,
      phases: [],
      transitions: [],
    });
    this.seriesSearch.setValue('Untitled phase series');
    this.activePairIndex.set(0);
    this.phasePanelOpen.set(true);
    this.refreshCanvasViews(false);
  }

  protected async refreshAll(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);
    try {
      const result = await firstValueFrom(forkJoin({
        blueprints: this.editor.getBlueprints(),
        phasedBlueprints: this.editor.getPhasedBlueprints(),
        tasks: this.taskDefinitions.getTaskDefinitions(),
      }));
      this.blueprints.set(result.blueprints);
      this.phasedBlueprints.set(result.phasedBlueprints);
      this.tasks.set(result.tasks);
    } catch {
      this.errorMessage.set('Unable to load phase series.');
    } finally {
      this.loading.set(false);
    }
  }

  protected async openSeries(definitionId: number): Promise<void> {
    this.openingSeriesId.set(definitionId);
    this.errorMessage.set(null);
    this.successMessage.set(null);
    this.bridgeDropMessage.set(null);
    try {
      const series = await firstValueFrom(this.editor.getPhasedBlueprintById(definitionId));
      const version = this.latestPhasedVersion(series);
      if (!version) {
        this.selectedSeries.set({
          id: series.id,
          name: series.name,
          versionId: null,
          versionNumber: null,
          phases: [],
          transitions: [],
        });
        this.seriesSearch.setValue(series.name);
        this.refreshCanvasViews(false);
        return;
      }

      const phases = await Promise.all(
        [...version.phases]
          .sort((left, right) => left.phaseIndex - right.phaseIndex)
          .map((phase, index) => this.createPhaseFromViewModel(phase, index)),
      );
      const phaseById = new Map(phases.map((phase) => [phase.phaseId, phase] as const));
      const transitions = version.transitions
        .map((transition) => this.createTransitionFromViewModel(transition, phaseById))
        .filter((transition): transition is PhaseSeriesTransitionCanvas => transition !== null);
      const draft = this.withAdjacentTransitions({
        id: series.id,
        name: series.name,
        versionId: version.phasedBlueprintVersionId,
        versionNumber: version.version,
        phases,
        transitions,
      });

      this.selectedSeries.set(draft);
      this.seriesSearch.setValue(series.name);
      this.activePairIndex.set(0);
      this.phasePanelOpen.set(false);
      this.refreshCanvasViews(true);
    } catch {
      this.errorMessage.set('Unable to open that phase series.');
    } finally {
      this.openingSeriesId.set(null);
    }
  }

  protected onSeriesOptionSelected(event: MatAutocompleteSelectedEvent): void {
    const definitionId = Number(event.option.value);
    if (Number.isInteger(definitionId)) {
      void this.openSeries(definitionId);
    }
  }

  protected focusSeriesPicker(): void {
    this.seriesSearch.setValue('');
    queueMicrotask(() => {
      this.seriesPickerInput()?.nativeElement.focus({ preventScroll: true });
      this.seriesPickerTrigger()?.openPanel();
    });
  }

  protected restoreSeriesPickerLabel(): void {
    const series = this.selectedSeries();
    if (series) {
      this.seriesSearch.setValue(series.name, { emitEvent: false });
    }
  }

  protected async addPhaseFromSelection(value: unknown): Promise<void> {
    const selection = this.parseBlueprintVersionValue(value);
    this.addPhaseSelection.set(typeof value === 'string' ? value : null);

    const series = this.selectedSeries();
    if (!selection || !series || series.phases.length >= 4) {
      this.addPhaseSelection.set(null);
      return;
    }

    this.errorMessage.set(null);
    try {
      const phase = await this.createPhaseFromBlueprintVersion(selection.blueprintId, selection.versionId, series.phases.length);
      const shouldCollapseSetup = series.phases.length < 2;
      const nextSeries = this.withAdjacentTransitions({
        ...series,
        phases: [...series.phases, phase],
      });
      this.selectedSeries.set(nextSeries);
      this.activePairIndex.set(Math.max(0, Math.min(this.activePairIndex(), nextSeries.phases.length - 2)));
      if (shouldCollapseSetup && nextSeries.phases.length >= 2) {
        this.phasePanelOpen.set(false);
      }
      this.refreshCanvasViews(true);
    } catch {
      this.errorMessage.set('Unable to load that blueprint version.');
    } finally {
      this.addPhaseSelection.set(null);
    }
  }

  protected async changePhaseVersion(index: number, value: unknown): Promise<void> {
    const selection = this.parseBlueprintVersionValue(value);
    const series = this.selectedSeries();
    const previousPhase = series?.phases[index];
    if (!selection || !series || !previousPhase) {
      return;
    }

    this.errorMessage.set(null);
    try {
      const phase = await this.createPhaseFromBlueprintVersion(selection.blueprintId, selection.versionId, index);
      const phases = series.phases.map((candidate, candidateIndex) =>
        candidateIndex === index
          ? {
              ...phase,
              phaseId: previousPhase.phaseId,
              name: previousPhase.name,
            }
          : candidate,
      );
      const changedSeries = {
        ...series,
        phases,
      };
      const nextSeries = this.withAdjacentTransitions({
        ...changedSeries,
        transitions: reconcileTransitionsForPhaseVersionChange(changedSeries, index),
      });
      this.selectedSeries.set(nextSeries);
      this.refreshCanvasViews(true);
    } catch {
      this.errorMessage.set('Unable to switch that phase version.');
    }
  }

  protected async updatePhaseToLatest(index: number): Promise<void> {
    const phase = this.selectedSeries()?.phases[index];
    if (!phase) {
      return;
    }

    const latest = this.latestBlueprintVersion(phase.blueprintDefinitionId);
    if (!latest) {
      return;
    }

    await this.changePhaseVersion(index, this.blueprintVersionValue(phase.blueprintDefinitionId, latest.blueprintVersionId));
  }

  protected removePhase(index: number): void {
    const series = this.selectedSeries();
    if (!series || series.phases.length <= 2) {
      return;
    }

    const phases = series.phases.filter((_, candidateIndex) => candidateIndex !== index);
    const nextSeries = this.withAdjacentTransitions({
      ...series,
      phases,
    });
    this.selectedSeries.set(nextSeries);
    this.activePairIndex.set(Math.max(0, Math.min(this.activePairIndex(), nextSeries.phases.length - 2)));
    this.refreshCanvasViews(true);
  }

  protected movePhase(index: number, direction: -1 | 1): void {
    const series = this.selectedSeries();
    const nextIndex = index + direction;
    if (!series || nextIndex < 0 || nextIndex >= series.phases.length) {
      return;
    }

    const phases = [...series.phases];
    const [phase] = phases.splice(index, 1);
    if (!phase) {
      return;
    }

    phases.splice(nextIndex, 0, phase);
    const nextSeries = this.withAdjacentTransitions({
      ...series,
      phases,
    });
    this.selectedSeries.set(nextSeries);
    this.activePairIndex.set(Math.max(0, Math.min(this.activePairIndex(), nextSeries.phases.length - 2)));
    this.refreshCanvasViews(true);
  }

  protected selectPair(value: unknown): void {
    const pairIndex = Number(value);
    const series = this.selectedSeries();
    if (!series || !Number.isInteger(pairIndex) || pairIndex < 0 || pairIndex >= series.phases.length - 1) {
      return;
    }

    this.activePairIndex.set(pairIndex);
    this.bridgeDropMessage.set(null);
    this.clearBridgeSelection();
    this.refreshCanvasViews(true);
  }

  protected setSplitMode(mode: PhaseSplitMode): void {
    if (this.splitMode() === mode) {
      return;
    }

    this.splitMode.set(mode);
    queueMicrotask(() => this.scheduleBridgeLineRefresh());
  }

  protected renameSelectedSeries(name: string): void {
    this.selectedSeries.update((series) =>
      series
        ? {
            ...series,
            name: name.slice(0, 100),
          }
        : series,
    );
  }

  protected async saveSeries(): Promise<void> {
    const currentSeries = this.selectedSeries();
    if (!currentSeries || this.saving()) {
      return;
    }

    if (!this.canSaveSeries()) {
      this.errorMessage.set(this.validationMessages()[0] ?? 'Resolve Data Bridge issues before saving.');
      return;
    }

    const phases = this.createPhasePayloads(currentSeries);
    const transitions = currentSeries.transitions.map((transition) => serializeTransitionMappings(transition));
    this.saving.set(true);
    this.errorMessage.set(null);
    this.successMessage.set(null);
    try {
      if (currentSeries.id === null) {
        const payload: PhasedBlueprintInputModel = {
          key: null,
          name: currentSeries.name.trim(),
          description: null,
          phases,
          transitions,
        };
        const created = await firstValueFrom(this.editor.createPhasedBlueprint(payload));
        await this.refreshAll();
        await this.openSeries(created.id);
      } else {
        const payload: PhasedBlueprintVersionInputModel = {
          phasedBlueprintDefinitionId: currentSeries.id,
          baseVersion: currentSeries.versionNumber,
          description: null,
          phases,
          transitions,
        };
        await firstValueFrom(this.editor.createPhasedBlueprintVersion(currentSeries.id, payload));
        await this.refreshAll();
        await this.openSeries(currentSeries.id);
      }

      this.successMessage.set('Phase series saved.');
    } catch {
      this.errorMessage.set('Unable to save the phase series.');
    } finally {
      this.saving.set(false);
    }
  }

  protected editPhaseBlueprint(phase: PhaseSeriesPhaseCanvas): void {
    void this.router.navigate(['/blueprints'], {
      queryParams: {
        blueprintId: phase.blueprintDefinitionId,
        versionId: phase.blueprintVersionId ?? undefined,
      },
    });
  }

  protected togglePhasePanel(): void {
    if (this.phasePanelOpen()) {
      this.closePhasePanel();
      return;
    }

    this.phasePanelOpen.set(true);
  }

  protected closePhasePanel(): void {
    if (this.phaseSetupRequired()) {
      this.phasePanelOpen.set(true);
      return;
    }

    this.phasePanelOpen.set(false);
  }

  protected onSourceBridgeDragStarted(event: CanvasOutputPropertyDragEvent): void {
    const bridgePosition = this.bridgePositionFromClientPoint(event.clientX, event.clientY);
    this.bridgeController.startSourceDrag(event, bridgePosition);
  }

  protected onSourceBridgeDragMoved(event: CanvasOutputPropertyDragEvent): void {
    const bridgePosition = this.bridgePositionFromClientPoint(event.clientX, event.clientY);
    this.bridgeController.moveSourceDrag(event, bridgePosition);
  }

  protected onSourceBridgeDragEnded(event: CanvasOutputPropertyDragEvent): void {
    this.finishSourceBridgeDrag(event.clientX, event.clientY, event.cancelled === true, event);
  }

  protected cancelBridgeDrag(): void {
    this.bridgeController.cancelDrag();
  }

  protected startBridgeOutputDrag(event: PointerEvent, mapping: DataBridgeMappingDraft): void {
    this.bridgeController.startBridgeOutputDrag(event, mapping, {
      complete: (mappingId, clientX, clientY, cancelled) =>
        this.finishBridgeOutputDrag(mappingId, clientX, clientY, cancelled),
    });
  }

  protected clearMappingTarget(mappingId: string): void {
    this.updateActiveTransition((transition) => clearDataBridgeMappingTarget(transition, mappingId));
    this.clearBridgeSelection();
  }

  protected removeMapping(mappingId: string): void {
    this.updateActiveTransition((transition) => removeDataBridgeMapping(transition, mappingId));
    this.clearBridgeSelection();
  }

  protected onCanvasViewportChanged(): void {
    this.scheduleBridgeLineRefresh();
  }

  protected phaseCanvasGraph(phase: PhaseSeriesPhaseCanvas): BlueprintCanvasGraph {
    return this.canvasPairPresenter.graph(phase, this.phaseCanvasRevision());
  }

  protected onSourceCanvasEvent(event: BlueprintCanvasEvent): void {
    this.canvasPairPresenter.handleSourceEvent(event, {
      viewportChanged: () => this.onCanvasViewportChanged(),
      sourceDragStarted: (drag) => this.onSourceBridgeDragStarted(drag),
      sourceDragMoved: (drag) => this.onSourceBridgeDragMoved(drag),
      sourceDragEnded: (drag) => this.onSourceBridgeDragEnded(drag),
    });
  }

  protected onDestinationCanvasEvent(event: BlueprintCanvasEvent): void {
    this.canvasPairPresenter.handleDestinationEvent(event, {
      viewportChanged: () => this.onCanvasViewportChanged(),
    });
  }

  protected resetSourceView(): void {
    this.canvasPairPresenter.reset(this.sourceCanvas());
  }

  protected zoomSourceIn(): void {
    this.canvasPairPresenter.zoomIn(this.sourceCanvas());
  }

  protected zoomSourceOut(): void {
    this.canvasPairPresenter.zoomOut(this.sourceCanvas());
  }

  protected resetDestinationView(): void {
    this.canvasPairPresenter.reset(this.destinationCanvas());
  }

  protected zoomDestinationIn(): void {
    this.canvasPairPresenter.zoomIn(this.destinationCanvas());
  }

  protected zoomDestinationOut(): void {
    this.canvasPairPresenter.zoomOut(this.destinationCanvas());
  }

  protected resetBothCanvases(): void {
    this.canvasPairPresenter.reset(this.sourceCanvas());
    this.canvasPairPresenter.reset(this.destinationCanvas());
  }

  protected phaseVersionValue(phase: PhaseSeriesPhaseCanvas): string | null {
    return phase.blueprintVersionId === null
      ? null
      : this.blueprintVersionValue(phase.blueprintDefinitionId, phase.blueprintVersionId);
  }

  protected phaseVersionLabel(phase: PhaseSeriesPhaseCanvas): string {
    return phase.blueprintVersionNumber
      ? `${phase.blueprintName} v${phase.blueprintVersionNumber}`
      : phase.blueprintName;
  }

  protected phaseHasUpdate(phase: PhaseSeriesPhaseCanvas): boolean {
    const latest = this.latestBlueprintVersion(phase.blueprintDefinitionId);
    return !!latest &&
      phase.blueprintVersionNumber !== null &&
      latest.version > phase.blueprintVersionNumber;
  }

  protected mappingIssueMessage(mappingId: string): string | null {
    return this.activeTransitionIssues().find((issue) => issue.mappingId === mappingId)?.message ?? null;
  }

  protected sourceAnchorTooltip(mapping: DataBridgeMappingDraft): string {
    return `${mapping.sourceNodeName}.${mapping.sourceProperty} (${mapping.propertyKind})`;
  }

  protected sourceAnchorAriaLabel(mapping: DataBridgeMappingDraft): string {
    const issue = this.mappingIssueMessage(mapping.id);
    return [
      `Data Bridge source ${mapping.sourceNodeName}.${mapping.sourceProperty}.`,
      `Type ${mapping.propertyKind}.`,
      issue ? `Issue: ${issue}` : 'Ready to connect to the next phase.',
    ].join(' ');
  }

  protected destinationAnchorTooltip(mapping: DataBridgeMappingDraft): string {
    return mapping.targetNodeName && mapping.targetProperty
      ? `${mapping.targetNodeName}.${mapping.targetProperty} (${mapping.propertyKind})`
      : `Disconnected (${mapping.propertyKind})`;
  }

  protected destinationAnchorAriaLabel(mapping: DataBridgeMappingDraft): string {
    const issue = this.mappingIssueMessage(mapping.id);
    const target = mapping.targetNodeName && mapping.targetProperty
      ? `Data Bridge destination ${mapping.targetNodeName}.${mapping.targetProperty}.`
      : `Data Bridge destination for ${mapping.sourceProperty} is disconnected.`;
    return [
      target,
      `Type ${mapping.propertyKind}.`,
      issue ? `Issue: ${issue}` : mapping.targetProperty ? 'Mapped.' : 'Drag this handle to a compatible destination input.',
    ].join(' ');
  }

  protected phaseBlueprintUseCount(blueprintDefinitionId: number): number {
    return this.selectedSeries()?.phases
      .filter((phase) => phase.blueprintDefinitionId === blueprintDefinitionId)
      .length ?? 0;
  }

  protected bridgeAnchorLeft(mapping: DataBridgeMappingDraft): number {
    return this.bridgeAnchorPosition(mapping);
  }

  protected bridgeAnchorPosition(mapping: DataBridgeMappingDraft): number {
    return this.bridgePreviewPositionPercent(mapping.bridgePosition);
  }

  protected bridgePreviewLeft(position: number | null | undefined): number {
    return this.bridgePreviewPositionPercent(position);
  }

  protected bridgePreviewPositionPercent(position: number | null | undefined): number {
    return normalizePhaseBridgePosition(position) * 100;
  }

  protected selectBridgeConnection(
    mappingId: string,
    side: BridgeSelection['side'],
    event?: Event,
  ): void {
    event?.preventDefault();
    event?.stopPropagation();
    this.bridgeController.select(mappingId, side);
  }

  protected clearBridgeSelection(): void {
    this.bridgeController.clearSelection();
  }

  protected isBridgeAnchorSelected(mappingId: string, side: BridgeSelection['side']): boolean {
    return this.bridgeController.isAnchorSelected(mappingId, side);
  }

  protected isBridgeCurveSelected(curve: BridgeCurve): boolean {
    return this.isBridgeAnchorSelected(curve.mappingId, curve.side);
  }

  protected bridgeDragTransform(drag: BridgeDragState): string {
    return `translate(${drag.clientX + 12}px, ${drag.clientY + 12}px)`;
  }

  protected trackSummary(_index: number, summary: PhaseSeriesSummary): number {
    return summary.id;
  }

  protected trackPhase(_index: number, phase: PhaseSeriesPhaseCanvas): string {
    return phase.phaseId;
  }

  protected trackMapping(_index: number, mapping: DataBridgeMappingDraft): string {
    return mapping.id;
  }

  private async createPhaseFromViewModel(
    phase: PhasedBlueprintVersionViewModel['phases'][number],
    index: number,
  ): Promise<PhaseSeriesPhaseCanvas> {
    const graph = await firstValueFrom(this.editor.loadBlueprintGraph(
      phase.blueprintDefinitionId,
      this.tasks(),
      phase.blueprintVersionId,
    ));
    const selectedVersion = this.resolveBlueprintVersion(graph.blueprint, phase.blueprintVersionId);
    const nodes = this.ensureNodeInstanceIds(graph.nodes);
    return {
      phaseId: phase.phaseId,
      name: phase.name || `Phase ${index + 1}`,
      blueprintName: phase.blueprintName || graph.blueprint.name,
      blueprintDefinitionId: phase.blueprintDefinitionId,
      blueprintVersionId: selectedVersion?.blueprintVersionId ?? phase.blueprintVersionId ?? null,
      blueprintVersionNumber: selectedVersion?.version ?? phase.blueprintVersionNumber ?? null,
      nodes,
      connections: this.rebindConnections(graph.connections, nodes),
    };
  }

  private async createPhaseFromBlueprintVersion(
    blueprintDefinitionId: number,
    blueprintVersionId: number,
    index: number,
  ): Promise<PhaseSeriesPhaseCanvas> {
    const graph = await firstValueFrom(this.editor.loadBlueprintGraph(
      blueprintDefinitionId,
      this.tasks(),
      blueprintVersionId,
    ));
    const version = this.resolveBlueprintVersion(graph.blueprint, blueprintVersionId);
    const nodes = this.ensureNodeInstanceIds(graph.nodes);
    return {
      phaseId: crypto.randomUUID(),
      name: `Phase ${index + 1}`,
      blueprintName: graph.blueprint.name,
      blueprintDefinitionId: graph.blueprint.id,
      blueprintVersionId: version?.blueprintVersionId ?? blueprintVersionId,
      blueprintVersionNumber: version?.version ?? null,
      nodes,
      connections: this.rebindConnections(graph.connections, nodes),
    };
  }

  private createTransitionFromViewModel(
    transition: PhasedBlueprintTransitionViewModel,
    phaseById: ReadonlyMap<string, PhaseSeriesPhaseCanvas>,
  ): PhaseSeriesTransitionCanvas | null {
    const sourcePhase = phaseById.get(transition.sourcePhaseId);
    const destinationPhase = phaseById.get(transition.destinationPhaseId);
    if (!sourcePhase || !destinationPhase) {
      return null;
    }

    return createTransitionDraft(
      transition.sourcePhaseId,
      transition.destinationPhaseId,
      transition.handoffMappings
        .map((mapping) => createDataBridgeMappingFromHandoff(mapping, sourcePhase, destinationPhase))
        .filter((mapping): mapping is DataBridgeMappingDraft => mapping !== null),
      transition.transitionId,
    );
  }

  private withAdjacentTransitions(series: EditablePhaseSeries): EditablePhaseSeries {
    const transitionByPair = new Map<string, PhaseSeriesTransitionCanvas>(series.transitions.map((transition) => [
      `${transition.sourcePhaseId}->${transition.destinationPhaseId}`,
      transition,
    ] as const));
    const transitions = series.phases.slice(0, -1).map((phase, index) => {
      const destinationPhase = series.phases[index + 1]!;
      const key = `${phase.phaseId}->${destinationPhase.phaseId}`;
      return transitionByPair.get(key) ?? createTransitionDraft(phase.phaseId, destinationPhase.phaseId);
    });

    return {
      ...series,
      transitions,
    };
  }

  private updateActiveTransition(updater: (transition: PhaseSeriesTransitionCanvas) => PhaseSeriesTransitionCanvas): void {
    const series = this.selectedSeries();
    const sourcePhase = this.activeSourcePhase();
    const destinationPhase = this.activeDestinationPhase();
    if (!series || !sourcePhase || !destinationPhase) {
      return;
    }

    const transitionIndex = series.transitions.findIndex((transition) =>
      transition.sourcePhaseId === sourcePhase.phaseId &&
      transition.destinationPhaseId === destinationPhase.phaseId);
    if (transitionIndex < 0) {
      return;
    }

    const transitions = series.transitions.map((transition, index) =>
      index === transitionIndex ? updater(transition) : transition);
    this.selectedSeries.set({
      ...series,
      transitions,
    });
    this.scheduleBridgeLineRefresh();
  }

  private finishBridgeOutputDrag(mappingId: string, clientX: number, clientY: number, cancelled: boolean): void {
    if (!this.bridgeController.prepareDestinationDrop(cancelled)) {
      return;
    }

    const destinationPhase = this.activeDestinationPhase();
    const target = this.destinationCanvas()?.bridge.hitTestInputPropertyAtClientPoint(clientX, clientY) ?? null;
    const transition = this.activeTransition();
    if (!destinationPhase || !target || !transition) {
      this.bridgeController.rejectDestinationDrop();
      return;
    }

    const result = completeDataBridgeMappingTarget(
      transition,
      destinationPhase,
      mappingId,
      this.createTargetProperty(target),
    );
    if (!result.accepted) {
      this.bridgeController.rejectDestinationDrop();
      return;
    }

    this.updateActiveTransition(() => result.transition);
    this.bridgeController.acceptDestinationDrop(mappingId);
  }

  private finishSourceBridgeDrag(
    clientX: number,
    clientY: number,
    cancelled: boolean,
    event?: CanvasOutputPropertyDragEvent,
  ): void {
    const bridgePosition = this.bridgePositionFromClientPoint(clientX, clientY);
    const result = this.bridgeController.finishSourceDrag({
      clientX,
      clientY,
      cancelled,
      bridgePosition,
      event,
    });
    if (result.kind === 'stageSource') {
      this.updateActiveTransition((transition) =>
        upsertDataBridgeSourceMapping(transition, result.source, result.bridgePosition));
    }
  }

  private createTargetProperty(hit: CanvasInputPropertyHit) {
    return {
      targetNodeId: hit.nodeId,
      targetBlueprintNodeId: hit.nodeInstanceId,
      targetNodeName: hit.nodeName,
      targetProperty: hit.propertyName,
      propertyKind: hit.propertyKind,
    };
  }

  private createPhasePayloads(series: EditablePhaseSeries): PhasedBlueprintPhaseInputModel[] {
    return series.phases.map((phase, index) => ({
      phaseId: phase.phaseId,
      phaseIndex: index,
      name: phase.name.trim() || `Phase ${index + 1}`,
      blueprintDefinitionId: phase.blueprintDefinitionId,
      blueprintVersionId: phase.blueprintVersionId,
      versionPolicy: 'Pinned',
    }));
  }

  private createSeriesSummary(series: PhasedBlueprintViewModel): PhaseSeriesSummary {
    const version = this.latestPhasedVersion(series);
    const phases = version
      ? [...version.phases].sort((left, right) => left.phaseIndex - right.phaseIndex)
      : [];
    const mappingCount = version?.transitions.reduce((total, transition) => total + transition.handoffMappings.length, 0) ?? 0;
    const updateAvailable = phases.some((phase) => {
      const latest = this.latestBlueprintVersion(phase.blueprintDefinitionId);
      return !!latest &&
        phase.blueprintVersionNumber !== null &&
        phase.blueprintVersionNumber !== undefined &&
        latest.version > phase.blueprintVersionNumber;
    });

    return {
      id: series.id,
      name: series.name,
      version: version?.version ?? null,
      phaseCount: phases.length,
      selectedBlueprints: phases
        .map((phase) => `${phase.blueprintName} v${phase.blueprintVersionNumber ?? '?'}`)
        .join(' -> '),
      bridgeStatus: mappingCount === 1 ? '1 Data Bridge mapping' : `${mappingCount} Data Bridge mappings`,
      updateAvailable,
    };
  }

  private latestPhasedVersion(series: PhasedBlueprintViewModel): PhasedBlueprintVersionViewModel | null {
    return [...series.versions].sort((left, right) => right.version - left.version)[0] ?? null;
  }

  private latestBlueprintVersion(blueprintDefinitionId: number): BlueprintVersionViewModel | null {
    const blueprint = this.blueprints().find((candidate) => candidate.id === blueprintDefinitionId);
    return blueprint ? this.sortBlueprintVersions(blueprint.versions)[0] ?? null : null;
  }

  private resolveBlueprintVersion(
    blueprint: BlueprintViewModel,
    blueprintVersionId: number | null | undefined,
  ): BlueprintVersionViewModel | null {
    return blueprintVersionId
      ? blueprint.versions.find((version) => version.blueprintVersionId === blueprintVersionId) ?? blueprint.versions[0] ?? null
      : blueprint.versions[0] ?? null;
  }

  private sortBlueprintVersions(versions: readonly BlueprintVersionViewModel[]): BlueprintVersionViewModel[] {
    return [...versions].sort((left, right) => right.version - left.version);
  }

  private ensureNodeInstanceIds(nodes: PhaseSeriesPhaseCanvas['nodes']): PhaseSeriesPhaseCanvas['nodes'] {
    return nodes.map((node) => node.instanceId ? node : node.withInstanceId(crypto.randomUUID()));
  }

  private rebindConnections(connections: readonly ConnectionModel[], nodes: readonly NodeModel[]): ConnectionModel[] {
    const nodeById = new Map(nodes.map((node) => [node.id, node] as const));
    return connections.flatMap((connection) => {
      const from = nodeById.get(connection.from.id);
      const to = nodeById.get(connection.to.id);
      if (!from || !to) {
        return [];
      }

      return [new ConnectionModel(
        from,
        to,
        connection.anchor,
        connection.side,
        connection.pos,
        connection.id,
        connection.propertyMappings.map((mapping) => ({ ...mapping })),
      )];
    });
  }

  private sourceCanvas(): BlueprintCanvasComponent | undefined {
    return this.phaseCanvases()[0];
  }

  private destinationCanvas(): BlueprintCanvasComponent | undefined {
    return this.phaseCanvases()[1];
  }

  private refreshCanvasViews(resetView: boolean): void {
    this.phaseCanvasRevision.update((revision) => revision + 1);
    if (resetView) {
      queueMicrotask(() => {
        this.resetBothCanvases();
        this.scheduleBridgeLineRefresh();
      });
      return;
    }

    this.scheduleBridgeLineRefresh();
  }

  private activeTransitionIndex(series: EditablePhaseSeries): number {
    const sourcePhase = series.phases[this.activePairIndex()];
    const destinationPhase = series.phases[this.activePairIndex() + 1];
    if (!sourcePhase || !destinationPhase) {
      return -1;
    }

    return series.transitions.findIndex((transition) =>
      transition.sourcePhaseId === sourcePhase.phaseId &&
      transition.destinationPhaseId === destinationPhase.phaseId);
  }

  private deleteSelectedBridgeConnection(): void {
    const effect = this.bridgeController.deleteSelectionEffect();
    if (!effect) {
      return;
    }

    if (effect.kind === 'removeMapping') {
      this.updateActiveTransition((transition) => removeDataBridgeMapping(transition, effect.mappingId));
    } else {
      this.updateActiveTransition((transition) => clearDataBridgeMappingTarget(transition, effect.mappingId));
    }
  }

  private bridgePositionFromClientPoint(clientX: number, clientY: number): number | null {
    return this.bridgeCurveFactory.bridgePositionFromClientPoint(
      clientX,
      clientY,
      this.bridgeDividerRect(),
      this.splitMode(),
    );
  }

  private setBridgeDrag(drag: BridgeDragState | null): void {
    this.bridgeController.setDrag(drag);
  }

  private scheduleBridgeLineRefresh(): void {
    if (this.bridgeLineFrameId !== null) {
      return;
    }

    this.bridgeLineFrameId = this.requestBridgeLineFrame(() => {
      this.bridgeLineFrameId = null;
      this.refreshBridgeLines();
    });
  }

  private refreshBridgeLines(): void {
    const workbenchRect = this.workbenchRect();
    const dividerRect = this.bridgeDividerRect();
    if (!workbenchRect || !dividerRect) {
      this.bridgeCurves.set([]);
      this.bridgeDragCurve.set(null);
      return;
    }

    const plan = this.bridgeCurveFactory.createCurvePlan({
      workbenchRect,
      dividerRect,
      splitMode: this.splitMode(),
      transition: this.activeTransition(),
      drag: this.bridgeDrag(),
      sourceCanvas: this.sourceCanvas(),
      destinationCanvas: this.destinationCanvas(),
      mappingIssueMessage: (mappingId) => this.mappingIssueMessage(mappingId),
    });
    this.bridgeCurves.set([...plan.curves]);
    this.bridgeDragCurve.set(plan.dragCurve);
  }

  private bridgeAnchorClientPoint(kind: 'input' | 'output', mappingId: string): { clientX: number; clientY: number } | null {
    const mapping = this.activeTransition()?.mappings.find((candidate) => candidate.id === mappingId) ?? null;
    return mapping ? this.bridgeClientPoint(kind, mapping.bridgePosition) : null;
  }

  private bridgeClientPoint(kind: 'input' | 'output', bridgePosition: number): { clientX: number; clientY: number } | null {
    return this.bridgeCurveFactory.bridgeClientPoint(kind, bridgePosition, this.bridgeDividerRect(), this.splitMode());
  }

  private createBridgeCurvePath(
    workbenchElement: HTMLElement | Pick<PhaseWorkbenchDomRect, 'left' | 'top'>,
    startClient: { clientX: number; clientY: number },
    endClient: { clientX: number; clientY: number },
    options: Parameters<PhaseBridgeCurveFactory['createBridgeCurvePath']>[3] = {},
  ): string {
    const workbenchRect = 'getBoundingClientRect' in workbenchElement
      ? workbenchElement.getBoundingClientRect()
      : workbenchElement;
    return this.bridgeCurveFactory.createBridgeCurvePath(workbenchRect, startClient, endClient, options);
  }

  private workbenchRect(): Pick<PhaseWorkbenchDomRect, 'left' | 'top'> | null {
    return this.workbench()?.nativeElement.getBoundingClientRect() ?? null;
  }

  private bridgeDividerRect(): PhaseWorkbenchDomRect | null {
    return this.bridgeDivider()?.nativeElement.getBoundingClientRect() ?? null;
  }

  private requestBridgeLineFrame(callback: FrameRequestCallback): number {
    return typeof requestAnimationFrame === 'function'
      ? requestAnimationFrame(callback)
      : globalThis.setTimeout(() => callback(globalThis.performance?.now?.() ?? Date.now()), 16) as unknown as number;
  }

  private cancelBridgeLineFrame(frameId: number): void {
    if (typeof cancelAnimationFrame === 'function') {
      cancelAnimationFrame(frameId);
      return;
    }

    globalThis.clearTimeout(frameId);
  }

  private isEditableTarget(target: EventTarget | null): boolean {
    if (typeof HTMLElement === 'undefined' || !(target instanceof HTMLElement)) {
      return false;
    }

    return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
  }

  private stopBridgeOutputDragListeners(): void {
    this.bridgeController.stopOutputDragListeners();
  }

  private blueprintVersionValue(blueprintId: number, versionId: number): string {
    return `${blueprintId}:${versionId}`;
  }

  private parseBlueprintVersionValue(value: unknown): { blueprintId: number; versionId: number } | null {
    if (typeof value !== 'string') {
      return null;
    }

    const [blueprintIdText, versionIdText] = value.split(':');
    const blueprintId = Number(blueprintIdText);
    const versionId = Number(versionIdText);
    return Number.isInteger(blueprintId) && Number.isInteger(versionId)
      ? { blueprintId, versionId }
      : null;
  }
}
