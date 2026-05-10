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
import {
  BlueprintCanvasComponent,
  CanvasInputPropertyHit,
  CanvasOutputPropertyDragEvent,
  CanvasViewportInsets,
} from '../components/blueprint-canvas.component';
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
import { NodeModel, TaskPropertyType } from '../domain/node-model';
import {
  DataBridgeMappingDraft,
  DataBridgeSourceProperty,
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

interface BridgeDragState {
  kind: 'source-output' | 'bridge-output';
  clientX: number;
  clientY: number;
  label: string;
  propertyKind: TaskPropertyType;
  mappingId: string | null;
  bridgePosition?: number | null;
  sourceNodeId?: number | null;
  sourceBlueprintNodeId?: string | null;
  sourceNodeName?: string | null;
  sourceChannelName?: string | null;
  sourceProperty?: string | null;
}

interface BridgeSelection {
  mappingId: string;
  side: 'source' | 'destination';
}

interface BridgeCurve {
  id: string;
  mappingId: string;
  side: BridgeSelection['side'];
  path: string;
  state: 'complete' | 'incomplete' | 'issue' | 'preview';
}

type BridgeCurveOutwardSide = 'left' | 'right';

type PhaseSplitMode = 'horizontal' | 'vertical';

interface BridgeCurvePathOptions {
  startOutward?: BridgeCurveOutwardSide;
  endOutward?: BridgeCurveOutwardSide;
}

type BridgeGuidanceTone = 'neutral' | 'info' | 'warning' | 'success';

interface BridgeGuidance {
  icon: string;
  text: string;
  tone: BridgeGuidanceTone;
}

const BRIDGE_NODE_PROTRUSION = 34;

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
  private bridgeOutputDragCleanup: (() => void) | null = null;
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
  protected readonly bridgeDropMessage = signal<string | null>(null);
  protected readonly bridgeDrag = signal<BridgeDragState | null>(null);
  protected readonly bridgePreviewPosition = signal<number | null>(null);
  protected readonly selectedBridgeConnection = signal<BridgeSelection | null>(null);
  protected readonly bridgeCurves = signal<BridgeCurve[]>([]);
  protected readonly bridgeDragCurve = signal<string | null>(null);
  protected readonly blueprints = signal<BlueprintViewModel[]>([]);
  protected readonly phasedBlueprints = signal<PhasedBlueprintViewModel[]>([]);
  protected readonly tasks = signal<ITaskDefinition[]>([]);
  protected readonly selectedSeries = signal<EditablePhaseSeries | null>(null);
  protected readonly activePairIndex = signal(0);
  protected readonly phaseCanvasRevision = signal(0);
  protected readonly splitMode = signal<PhaseSplitMode>('horizontal');

  protected readonly canvasBackgroundEffect = 'stitch' as const;
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

  protected readonly bridgeMappingCountLabel = computed(() => {
    const count = this.bridgeMappingCount();
    if (count === 0) {
      return null;
    }

    if (this.incompleteBridgeMappingCount() === 0) {
      return count === 1 ? '1 completed mapping' : `${count} completed mappings`;
    }

    return count === 1 ? '1 staged mapping' : `${count} staged mappings`;
  });

  protected readonly bridgeGuidance = computed<BridgeGuidance>(() => {
    const dropMessage = this.bridgeDropMessage();
    if (dropMessage) {
      return {
        icon: 'warning',
        text: dropMessage,
        tone: 'warning',
      };
    }

    const drag = this.bridgeDrag();
    if (drag?.kind === 'source-output') {
      return {
        icon: 'vertical_align_center',
        text: 'Drop to stage this property.',
        tone: 'info',
      };
    }

    if (drag?.kind === 'bridge-output') {
      return {
        icon: 'input',
        text: 'Drop on a compatible destination input.',
        tone: 'info',
      };
    }

    if (this.bridgeMappingCount() > 0) {
      if (this.incompleteBridgeMappingCount() === 0) {
        return {
          icon: 'check_circle',
          text: this.bridgeMappingCount() === 1 ? 'Mapping complete.' : 'Mappings complete.',
          tone: 'success',
        };
      }

      return {
        icon: 'hub',
        text: 'Drag the lower handle to a compatible destination input.',
        tone: 'warning',
      };
    }

    return {
      icon: 'drag_indicator',
      text: 'Drag an expanded source output here.',
      tone: 'neutral',
    };
  });

  protected readonly bridgeGuidanceLabel = computed(() => {
    const countLabel = this.bridgeMappingCountLabel();
    return countLabel
      ? `${this.bridgeGuidance().text} ${countLabel}.`
      : this.bridgeGuidance().text;
  });

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
    this.bridgeDropMessage.set(null);
    const bridgePosition = this.bridgePositionFromClientPoint(event.clientX, event.clientY);
    this.bridgePreviewPosition.set(bridgePosition);
    this.setBridgeDrag(this.createSourceDragState(event, bridgePosition));
  }

  protected onSourceBridgeDragMoved(event: CanvasOutputPropertyDragEvent): void {
    const bridgePosition = this.bridgePositionFromClientPoint(event.clientX, event.clientY);
    this.bridgePreviewPosition.set(bridgePosition);
    this.setBridgeDrag(this.createSourceDragState(event, bridgePosition));
  }

  protected onSourceBridgeDragEnded(event: CanvasOutputPropertyDragEvent): void {
    this.finishSourceBridgeDrag(event.clientX, event.clientY, event.cancelled === true, event);
  }

  protected cancelBridgeDrag(): void {
    this.stopBridgeOutputDragListeners();
    this.setBridgeDrag(null);
    this.bridgePreviewPosition.set(null);
    this.bridgeDropMessage.set(null);
  }

  protected startBridgeOutputDrag(event: PointerEvent, mapping: DataBridgeMappingDraft): void {
    event.preventDefault();
    event.stopPropagation();
    this.bridgeDropMessage.set(null);
    this.stopBridgeOutputDragListeners();
    this.setBridgeDrag(this.createBridgeOutputDragState(mapping, event.clientX, event.clientY));

    const startClientX = event.clientX;
    const startClientY = event.clientY;
    let movedBeyondClick = false;
    const onMove = (moveEvent: PointerEvent) => {
      movedBeyondClick ||= Math.abs(moveEvent.clientX - startClientX) > 3 || Math.abs(moveEvent.clientY - startClientY) > 3;
      this.setBridgeDrag(this.createBridgeOutputDragState(mapping, moveEvent.clientX, moveEvent.clientY));
    };
    const onUp = (upEvent: PointerEvent) => {
      if (!movedBeyondClick) {
        this.stopBridgeOutputDragListeners();
        this.setBridgeDrag(null);
        this.selectBridgeConnection(mapping.id, 'destination');
        return;
      }

      this.finishBridgeOutputDrag(mapping.id, upEvent.clientX, upEvent.clientY, false);
    };
    const onCancel = (cancelEvent: PointerEvent) => {
      this.finishBridgeOutputDrag(mapping.id, cancelEvent.clientX, cancelEvent.clientY, true);
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp, { once: true });
    window.addEventListener('pointercancel', onCancel, { once: true });
    this.bridgeOutputDragCleanup = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onCancel);
      this.bridgeOutputDragCleanup = null;
    };
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

  protected resetSourceView(): void {
    this.sourceCanvas()?.resetView();
  }

  protected zoomSourceIn(): void {
    this.sourceCanvas()?.zoomIn();
  }

  protected zoomSourceOut(): void {
    this.sourceCanvas()?.zoomOut();
  }

  protected resetDestinationView(): void {
    this.destinationCanvas()?.resetView();
  }

  protected zoomDestinationIn(): void {
    this.destinationCanvas()?.zoomIn();
  }

  protected zoomDestinationOut(): void {
    this.destinationCanvas()?.zoomOut();
  }

  protected resetBothCanvases(): void {
    this.sourceCanvas()?.resetView();
    this.destinationCanvas()?.resetView();
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
    return this.normalizeBridgePosition(position) * 100;
  }

  protected selectBridgeConnection(
    mappingId: string,
    side: BridgeSelection['side'],
    event?: Event,
  ): void {
    event?.preventDefault();
    event?.stopPropagation();
    this.selectedBridgeConnection.set({ mappingId, side });
  }

  protected clearBridgeSelection(): void {
    this.selectedBridgeConnection.set(null);
  }

  protected isBridgeAnchorSelected(mappingId: string, side: BridgeSelection['side']): boolean {
    const selection = this.selectedBridgeConnection();
    return selection?.mappingId === mappingId && selection.side === side;
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
    this.stopBridgeOutputDragListeners();
    this.setBridgeDrag(null);
    if (cancelled) {
      return;
    }

    const destinationPhase = this.activeDestinationPhase();
    const target = this.destinationCanvas()?.hitTestInputPropertyAtClientPoint(clientX, clientY) ?? null;
    const transition = this.activeTransition();
    if (!destinationPhase || !target || !transition) {
      this.bridgeDropMessage.set('Drop on a compatible destination input.');
      return;
    }

    const result = completeDataBridgeMappingTarget(
      transition,
      destinationPhase,
      mappingId,
      this.createTargetProperty(target),
    );
    if (!result.accepted) {
      this.bridgeDropMessage.set('Drop on a compatible destination input.');
      return;
    }

    this.updateActiveTransition(() => result.transition);
    this.selectBridgeConnection(mappingId, 'destination');
    this.bridgeDropMessage.set(null);
  }

  private finishSourceBridgeDrag(
    clientX: number,
    clientY: number,
    cancelled: boolean,
    event?: CanvasOutputPropertyDragEvent,
  ): void {
    const drag = this.bridgeDrag();
    if (drag?.kind !== 'source-output' && !event) {
      return;
    }

    const bridgePosition = this.bridgePositionFromClientPoint(clientX, clientY);
    this.setBridgeDrag(null);
    this.bridgePreviewPosition.set(null);
    if (cancelled) {
      return;
    }

    if (bridgePosition === null) {
      this.bridgeDropMessage.set('Drop on the bridge.');
      return;
    }

    const sourceProperty = event?.propertyName ?? drag?.sourceProperty ?? null;
    if (!sourceProperty) {
      this.bridgeDropMessage.set('Drop on the bridge.');
      return;
    }

    const source: DataBridgeSourceProperty = {
      sourceNodeId: event?.nodeId ?? drag?.sourceNodeId ?? null,
      sourceBlueprintNodeId: event?.nodeInstanceId ?? drag?.sourceBlueprintNodeId ?? null,
      sourceNodeName: event?.nodeName ?? drag?.sourceNodeName ?? drag?.label ?? 'Source',
      sourceChannelName: event?.channelName ?? drag?.sourceChannelName ?? null,
      sourceProperty,
      propertyKind: event?.propertyKind ?? drag?.propertyKind ?? 'String',
    };
    this.updateActiveTransition((transition) => upsertDataBridgeSourceMapping(transition, source, bridgePosition));
    this.bridgeDropMessage.set(null);
    this.scheduleBridgeLineRefresh();
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
    const selection = this.selectedBridgeConnection();
    if (!selection) {
      return;
    }

    if (selection.side === 'source') {
      this.updateActiveTransition((transition) => removeDataBridgeMapping(transition, selection.mappingId));
    } else {
      this.updateActiveTransition((transition) => clearDataBridgeMappingTarget(transition, selection.mappingId));
    }

    this.clearBridgeSelection();
  }

  private bridgePositionFromClientPoint(clientX: number, clientY: number): number | null {
    const element = this.bridgeDivider()?.nativeElement;
    if (!element) {
      return null;
    }

    const rect = element.getBoundingClientRect();
    if (this.splitMode() === 'vertical') {
      const horizontalHitSlop = 24;
      if (clientX < rect.left - horizontalHitSlop || clientX > rect.right + horizontalHitSlop) {
        return null;
      }

      if (clientY < rect.top || clientY > rect.bottom || rect.height <= 0) {
        return null;
      }

      return this.normalizeBridgePosition((clientY - rect.top) / rect.height);
    }

    const verticalHitSlop = 24;
    if (clientY < rect.top - verticalHitSlop || clientY > rect.bottom + verticalHitSlop) {
      return null;
    }

    if (clientX < rect.left || clientX > rect.right || rect.width <= 0) {
      return null;
    }

    return this.normalizeBridgePosition((clientX - rect.left) / rect.width);
  }

  private createSourceDragState(
    event: CanvasOutputPropertyDragEvent,
    bridgePosition: number | null,
  ): BridgeDragState {
    return {
      kind: 'source-output',
      clientX: event.clientX,
      clientY: event.clientY,
      label: `${event.nodeName}.${event.propertyName}`,
      propertyKind: event.propertyKind,
      mappingId: null,
      bridgePosition,
      sourceNodeId: event.nodeId,
      sourceBlueprintNodeId: event.nodeInstanceId,
      sourceNodeName: event.nodeName,
      sourceChannelName: event.channelName,
      sourceProperty: event.propertyName,
    };
  }

  private createBridgeOutputDragState(mapping: DataBridgeMappingDraft, clientX: number, clientY: number): BridgeDragState {
    return {
      kind: 'bridge-output',
      clientX,
      clientY,
      label: mapping.sourceProperty,
      propertyKind: mapping.propertyKind,
      mappingId: mapping.id,
    };
  }

  private setBridgeDrag(drag: BridgeDragState | null): void {
    this.bridgeDrag.set(drag);
    this.scheduleBridgeLineRefresh();
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
    const workbenchElement = this.workbench()?.nativeElement;
    const transition = this.activeTransition();
    if (!workbenchElement || !transition) {
      this.bridgeCurves.set([]);
      this.bridgeDragCurve.set(null);
      return;
    }

    const curves: BridgeCurve[] = [];
    const sourceCanvas = this.sourceCanvas();
    const destinationCanvas = this.destinationCanvas();
    for (const mapping of transition.mappings) {
      const sourcePoint = mapping.sourceNodeId !== null && mapping.sourceChannelName !== null
        ? this.visibleCanvasPoint(sourceCanvas, sourceCanvas?.outputPropertyClientPoint(
            mapping.sourceNodeId,
            mapping.sourceChannelName,
            mapping.sourceProperty,
          ) ?? null)
        : null;
      const bridgeInputPoint = this.bridgeAnchorClientPoint('input', mapping.id);
      if (sourcePoint && bridgeInputPoint) {
        curves.push({
          id: `${mapping.id}:input`,
          mappingId: mapping.id,
          side: 'source',
          path: this.createBridgeCurvePath(workbenchElement, sourcePoint, bridgeInputPoint, {
            startOutward: 'right',
          }),
          state: this.mappingIssueMessage(mapping.id) ? 'issue' : 'complete',
        });
      }

      const bridgeOutputPoint = this.bridgeAnchorClientPoint('output', mapping.id);
      const targetPoint = mapping.targetNodeId !== null && mapping.targetProperty
        ? this.visibleCanvasPoint(
            destinationCanvas,
            destinationCanvas?.inputPropertyClientPoint(mapping.targetNodeId, mapping.targetProperty) ?? null,
          )
        : null;
      if (bridgeOutputPoint && targetPoint) {
        curves.push({
          id: `${mapping.id}:output`,
          mappingId: mapping.id,
          side: 'destination',
          path: this.createBridgeCurvePath(workbenchElement, bridgeOutputPoint, targetPoint, {
            endOutward: 'left',
          }),
          state: this.mappingIssueMessage(mapping.id) ? 'issue' : 'complete',
        });
      }
    }

    this.bridgeCurves.set(curves);
    this.bridgeDragCurve.set(this.createBridgeDragCurvePath(workbenchElement));
  }

  private createBridgeDragCurvePath(workbenchElement: HTMLElement): string | null {
    const drag = this.bridgeDrag();
    if (!drag) {
      return null;
    }

    if (drag.kind === 'source-output' && drag.sourceNodeId !== undefined && drag.sourceChannelName !== undefined && drag.sourceProperty) {
      const sourceCanvas = this.sourceCanvas();
      const sourcePoint = this.visibleCanvasPoint(sourceCanvas, sourceCanvas?.outputPropertyClientPoint(
        drag.sourceNodeId ?? -1,
        drag.sourceChannelName,
        drag.sourceProperty,
      ) ?? null);
      const endPoint = drag.bridgePosition === null || drag.bridgePosition === undefined
        ? { clientX: drag.clientX, clientY: drag.clientY }
        : this.bridgeClientPoint('input', drag.bridgePosition);
      return sourcePoint && endPoint
        ? this.createBridgeCurvePath(workbenchElement, sourcePoint, endPoint, { startOutward: 'right' })
        : null;
    }

    if (drag.kind === 'bridge-output' && drag.mappingId) {
      const bridgeOutputPoint = this.bridgeAnchorClientPoint('output', drag.mappingId);
      const endPoint = { clientX: drag.clientX, clientY: drag.clientY };
      return bridgeOutputPoint ? this.createBridgeCurvePath(workbenchElement, bridgeOutputPoint, endPoint) : null;
    }

    return null;
  }

  private visibleCanvasPoint(
    canvas: BlueprintCanvasComponent | undefined,
    point: { clientX: number; clientY: number } | null,
  ): { clientX: number; clientY: number } | null {
    return canvas && point && canvas.containsClientPoint(point) ? point : null;
  }

  private bridgeAnchorClientPoint(kind: 'input' | 'output', mappingId: string): { clientX: number; clientY: number } | null {
    const mapping = this.activeTransition()?.mappings.find((candidate) => candidate.id === mappingId) ?? null;
    return mapping ? this.bridgeClientPoint(kind, mapping.bridgePosition) : null;
  }

  private bridgeClientPoint(kind: 'input' | 'output', bridgePosition: number): { clientX: number; clientY: number } | null {
    const divider = this.bridgeDivider()?.nativeElement;
    if (!divider) {
      return null;
    }

    const rect = divider.getBoundingClientRect();
    if (this.splitMode() === 'vertical') {
      return {
        clientX: kind === 'input' ? rect.left : rect.right,
        clientY: rect.top + rect.height * this.normalizeBridgePosition(bridgePosition),
      };
    }

    return {
      clientX: rect.left + rect.width * this.normalizeBridgePosition(bridgePosition),
      clientY: kind === 'input' ? rect.top : rect.bottom,
    };
  }

  private createBridgeCurvePath(
    workbenchElement: HTMLElement,
    startClient: { clientX: number; clientY: number },
    endClient: { clientX: number; clientY: number },
    options: BridgeCurvePathOptions = {},
  ): string {
    const start = this.toWorkbenchPoint(workbenchElement, startClient);
    const end = this.toWorkbenchPoint(workbenchElement, endClient);
    const direction = end.y >= start.y ? 1 : -1;
    const verticalDistance = Math.abs(end.y - start.y);
    const controlOffset = Math.max(42, Math.min(220, verticalDistance * 0.62));
    const firstControl = options.startOutward
      ? this.protrudeBridgeCurvePoint(start, options.startOutward)
      : { x: start.x, y: start.y + controlOffset * direction };
    const secondControl = options.endOutward
      ? this.protrudeBridgeCurvePoint(end, options.endOutward)
      : { x: end.x, y: end.y - controlOffset * direction };

    return [
      `M ${this.roundPathCoordinate(start.x)} ${this.roundPathCoordinate(start.y)}`,
      `C ${this.roundPathCoordinate(firstControl.x)} ${this.roundPathCoordinate(firstControl.y)}`,
      `${this.roundPathCoordinate(secondControl.x)} ${this.roundPathCoordinate(secondControl.y)}`,
      `${this.roundPathCoordinate(end.x)} ${this.roundPathCoordinate(end.y)}`,
    ].join(' ');
  }

  private protrudeBridgeCurvePoint(
    point: { x: number; y: number },
    side: BridgeCurveOutwardSide,
  ): { x: number; y: number } {
    return {
      x: point.x + (side === 'right' ? BRIDGE_NODE_PROTRUSION : -BRIDGE_NODE_PROTRUSION),
      y: point.y,
    };
  }

  private toWorkbenchPoint(
    workbenchElement: HTMLElement,
    point: { clientX: number; clientY: number },
  ): { x: number; y: number } {
    const rect = workbenchElement.getBoundingClientRect();
    return {
      x: point.clientX - rect.left,
      y: point.clientY - rect.top,
    };
  }

  private roundPathCoordinate(value: number): number {
    return Math.round(value * 10) / 10;
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

  private normalizeBridgePosition(value: number | null | undefined): number {
    if (typeof value !== 'number' || !Number.isFinite(value)) {
      return 0.5;
    }

    return Math.min(1, Math.max(0, value));
  }

  private isEditableTarget(target: EventTarget | null): boolean {
    if (typeof HTMLElement === 'undefined' || !(target instanceof HTMLElement)) {
      return false;
    }

    return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
  }

  private stopBridgeOutputDragListeners(): void {
    this.bridgeOutputDragCleanup?.();
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
