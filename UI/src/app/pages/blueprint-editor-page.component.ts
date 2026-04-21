import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  TemplateRef,
  computed,
  effect,
  inject,
  untracked,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  MatAutocompleteModule,
  MatAutocompleteSelectedEvent,
  MatAutocompleteTrigger,
} from '@angular/material/autocomplete';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import {
  BlueprintEditorService,
  BlueprintVersionViewModel,
  BlueprintViewModel,
} from '../domain/blueprint-editor.service';
import { BlueprintEditorStore } from '../domain/blueprint-editor.store';
import { BuilderInputService } from '../domain/builder-input.service';
import { TaskDefinitionsService } from '../domain/task-definitions.service';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';
import { BlueprintCanvasComponent } from '../components/blueprint-canvas.component';
import {
  ConnectionBehaviorSummary,
  ConnectionModel,
  buildConnectionMappingSelections,
  buildConnectionMappingsFromSelections,
  describeConnectionBehavior,
} from '../domain/connection-model';
import { NodeModel } from '../domain/node-model';
import {
  PropertyOverrideEditorComponent,
  PropertyOverrideEditorState,
} from '../components/property-override-editor.component';

interface TaskLibraryItem {
  task: ITaskDefinition;
  icon: string;
  categoryLabel: string;
  description: string;
  tone: 'trigger' | 'logic' | 'action';
  searchText: string;
}

interface SummaryChip {
  icon: string;
  label: string;
  tone: 'default' | 'mapped' | 'fanOut';
}

type BlueprintPickerMode = 'search' | 'selected' | 'draft' | 'draft-edit';
type BlueprintPickerReturnMode = 'selected' | 'draft' | null;

interface BlueprintPickerSuggestion {
  blueprint: BlueprintViewModel;
  isActive: boolean;
}

const BLUEPRINT_NAME_MAX_LENGTH = 100;
const DEFAULT_DRAFT_BLUEPRINT_NAME = 'Draft Blueprint';

@Component({
  selector: 'app-blueprint-editor-page',
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatCardModule,
    MatChipsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTooltipModule,
    BlueprintCanvasComponent,
    PropertyOverrideEditorComponent,
  ],
  templateUrl: './blueprint-editor-page.component.html',
  styleUrl: './blueprint-editor-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BlueprintEditorPageComponent {
  protected readonly Math = Math;
  protected readonly canvasBackgroundEffect = 'stitch' as const;

  protected readonly editor = inject(BlueprintEditorService);
  protected readonly editorStore = inject(BlueprintEditorStore);
  private readonly dialog = inject(MatDialog);
  private readonly taskDefinitions = inject(TaskDefinitionsService);
  private readonly builderInput = inject(BuilderInputService);
  private readonly canvasViewport = viewChild(BlueprintCanvasComponent);
  private readonly blueprintPickerInput = viewChild<ElementRef<HTMLInputElement>>('blueprintPickerInput');
  private readonly blueprintPickerTrigger = viewChild(MatAutocompleteTrigger);
  private readonly blueprintNameInput = viewChild<ElementRef<HTMLInputElement>>('blueprintNameInput');
  private readonly mappingDialogTemplate = viewChild<TemplateRef<unknown>>('mappingDialogTemplate');
  private readonly designTimeOverrideDialogTemplate = viewChild<TemplateRef<unknown>>('designTimeOverrideDialogTemplate');
  private readonly gridSize = 20;
  private mappingDialogRef: MatDialogRef<unknown> | null = null;
  private designTimeOverrideDialogRef: MatDialogRef<unknown> | null = null;

  protected readonly blueprintNameMaxLength = BLUEPRINT_NAME_MAX_LENGTH;
  protected readonly saving = signal(false);
  protected readonly blueprints = signal<BlueprintViewModel[]>([]);
  protected readonly selectedBlueprintId = signal<number | null>(null);
  protected readonly selectedBlueprintVersionId = signal<number | null>(null);
  protected readonly drawerCollapsed = signal(false);
  protected readonly compactDrawerViewport = signal(this.isCompactDrawerViewport());
  protected readonly pickerMode = signal<BlueprintPickerMode>('search');
  protected readonly pickerPanelOpen = signal(false);
  protected readonly committedBlueprintName = signal('');
  protected readonly blueprintNameDraft = signal('');
  protected readonly isEditingBlueprintName = signal(false);
  protected readonly blueprintNameTouched = signal(false);
  protected readonly mappingModalConnectionId = signal<string | null>(null);
  protected readonly mappingModalSelections = signal<Record<string, string>>({});
  protected readonly designTimeOverrideModalNodeId = signal<number | null>(null);
  protected readonly designTimeOverrideState = signal<PropertyOverrideEditorState>({
    editorId: 'design-time',
    overrides: {},
    valid: true,
  });
  private readonly designTimeOverrideModalRevision = signal(0);
  private readonly pickerReturnMode = signal<BlueprintPickerReturnMode>(null);
  private readonly pickerSelectionInProgress = signal(false);
  private readonly pickerFocusRequested = signal(false);
  protected readonly tasks = toSignal(this.taskDefinitions.getTaskDefinitions(), { initialValue: [] as ITaskDefinition[] });
  protected readonly pluginLibrarySearch = new FormControl('', { nonNullable: true });
  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
  protected readonly blueprintSearchQuery = toSignal(this.blueprintSearch.valueChanges, {
    initialValue: this.blueprintSearch.value,
  });
  protected readonly pluginLibrarySearchQuery = toSignal(this.pluginLibrarySearch.valueChanges, {
    initialValue: this.pluginLibrarySearch.value,
  });
  protected readonly zoomPercent = computed(() => this.canvasViewport()?.zoomPercent() ?? 100);
  protected readonly taskLibraryItems = computed(() =>
    this.buildTaskLibraryItems(this.tasks()),
  );
  protected readonly filteredTaskLibraryItems = computed(() =>
    this.filterTaskLibraryItems(this.taskLibraryItems(), this.pluginLibrarySearchQuery()),
  );
  protected readonly selectedBlueprint = computed<BlueprintViewModel | null>(() =>
    this.blueprints().find((blueprint) => blueprint.id === this.selectedBlueprintId()) ?? null,
  );
  protected readonly selectedBlueprintVersions = computed<BlueprintVersionViewModel[]>(() =>
    [...(this.selectedBlueprint()?.versions ?? [])].sort((left, right) => right.version - left.version),
  );
  protected readonly selectedBlueprintVersion = computed<BlueprintVersionViewModel | null>(() => {
    const versions = this.selectedBlueprintVersions();
    if (versions.length === 0) {
      return null;
    }

    const selectedVersionId = this.selectedBlueprintVersionId();
    if (selectedVersionId === null) {
      return versions[0];
    }

    return versions.find((version) => version.blueprintVersionId === selectedVersionId) ?? versions[0];
  });
  protected readonly showVersionPicker = computed(() =>
    this.selectedBlueprintId() !== null && this.selectedBlueprintVersions().length > 0,
  );
  protected readonly isBlueprintNameReadonly = computed(() => this.selectedBlueprintId() !== null);
  protected readonly isDraftPickerMode = computed(() => {
    const mode = this.pickerMode();
    return mode === 'draft' || mode === 'draft-edit';
  });
  protected readonly isPickerSearchMode = computed(() => this.pickerMode() === 'search');
  protected readonly normalizedBlueprintName = computed(() =>
    (this.isEditingBlueprintName() ? this.blueprintNameDraft() : this.committedBlueprintName()).trim(),
  );
  protected readonly hasBlueprintName = computed(() => this.normalizedBlueprintName().length > 0);
  protected readonly draftBlueprintDisplayName = computed(() =>
    this.committedBlueprintName().trim() || DEFAULT_DRAFT_BLUEPRINT_NAME,
  );
  protected readonly selectedBlueprintName = computed(() =>
    this.committedBlueprintName().trim() || 'Untitled Blueprint',
  );
  protected readonly pickerDisplayName = computed(() =>
    this.isDraftPickerMode() ? this.draftBlueprintDisplayName() : this.selectedBlueprintName(),
  );
  protected readonly activeNodeCount = computed(() => this.editorStore.nodes().length);
  protected readonly selectedNode = computed<NodeModel | null>(() => {
    const selectedIds = this.editorStore.selectedNodeIds();
    if (selectedIds.length !== 1) {
      return null;
    }

    return this.editorStore.nodes().find((node) => node.id === selectedIds[0]) ?? null;
  });
  protected readonly selectedConnection = computed<ConnectionModel | null>(() => {
    const selectedIds = this.editorStore.selectedConnectionIds();
    if (selectedIds.length !== 1) {
      return null;
    }

    return this.editorStore.connections().find((connection) => connection.id === selectedIds[0]) ?? null;
  });
  protected readonly mappingModalConnection = computed<ConnectionModel | null>(() =>
    this.editorStore.connections().find((connection) => connection.id === this.mappingModalConnectionId()) ?? null,
  );
  protected readonly mappingModalBehavior = computed<ConnectionBehaviorSummary | null>(() => {
    const connection = this.mappingModalConnection();
    if (!connection) {
      return null;
    }

    return describeConnectionBehavior(connection, this.editorStore.connections());
  });
  protected readonly mappingModalChips = computed(() =>
    this.buildConnectionBehaviorChips(this.mappingModalBehavior()),
  );
  protected readonly mappingModalTargetProperties = computed(() =>
    Object.keys(this.mappingModalConnection()?.to.inputProperties ?? {}).sort((left, right) => left.localeCompare(right)),
  );
  protected readonly mappingModalSourceProperties = computed(() =>
    Object.keys(this.mappingModalConnection()?.from.outputPropertiesForAnchor(this.mappingModalConnection()?.anchor ?? -1) ?? {})
      .sort((left, right) => left.localeCompare(right)),
  );
  protected readonly designTimeOverrideModalNode = computed<NodeModel | null>(() =>
    this.editorStore.nodes().find((node) => node.id === this.designTimeOverrideModalNodeId()) ?? null,
  );
  protected readonly designTimeOverrideResetKey = computed(() =>
    `${this.designTimeOverrideModalNodeId() ?? 'none'}:${this.designTimeOverrideModalRevision()}`,
  );
  protected readonly canUndo = this.editorStore.canUndo;
  protected readonly canRedo = this.editorStore.canRedo;
  protected readonly canCopy = this.editorStore.canCopy;
  protected readonly canPaste = this.editorStore.canPaste;
  protected readonly canDelete = this.editorStore.canDelete;
  protected readonly blueprintNameError = computed(() =>
    this.blueprintNameTouched() && this.isDraftPickerMode() && !this.hasBlueprintName()
      ? 'Blueprint name is required.'
      : null,
  );
  protected readonly saveButtonLabel = computed(() => {
    if (this.saving()) {
      return 'Saving...';
    }

    return this.isBlueprintNameReadonly() ? 'Save New Version' : 'Create Blueprint';
  });
  protected readonly canSave = computed(() =>
    !this.saving() &&
    this.activeNodeCount() > 0 &&
    this.hasBlueprintName(),
  );
  protected readonly pluginLibraryDrawerMode = computed<'overlay' | 'overlay-with-backdrop'>(() =>
    this.compactDrawerViewport() ? 'overlay-with-backdrop' : 'overlay',
  );
  protected readonly pluginLibraryHasBackdrop = computed(() =>
    this.pluginLibraryDrawerMode() === 'overlay-with-backdrop',
  );
  protected readonly pickerSuggestions = computed<BlueprintPickerSuggestion[]>(() => {
    const filteredBlueprints = this.filterBlueprints(this.blueprints(), this.blueprintSearchQuery());
    const activeBlueprintId = this.selectedBlueprintId();
    if (activeBlueprintId === null) {
      return filteredBlueprints.map((blueprint) => ({
        blueprint,
        isActive: false,
      }));
    }

    const activeBlueprint = filteredBlueprints.find((blueprint) => blueprint.id === activeBlueprintId);
    const orderedBlueprints = activeBlueprint
      ? [activeBlueprint, ...filteredBlueprints.filter((blueprint) => blueprint.id !== activeBlueprintId)]
      : filteredBlueprints;

    return orderedBlueprints.map((blueprint) => ({
      blueprint,
      isActive: blueprint.id === activeBlueprintId,
    }));
  });

  constructor() {
    void this.builderInput;
    effect(() => {
      if (this.pickerMode() !== 'search' || !this.pickerFocusRequested()) {
        return;
      }

      this.scheduleUiTask(() => {
        const input = this.blueprintPickerInput()?.nativeElement;
        input?.focus();
        this.blueprintPickerTrigger()?.openPanel();
        this.pickerFocusRequested.set(false);
      });
    });

    effect(() => {
      if (!this.isEditingBlueprintName()) {
        return;
      }

      this.scheduleUiTask(() => {
        const input = this.blueprintNameInput()?.nativeElement;
        input?.focus();
        input?.select();
      });
    });

    effect(() => {
      const modalConnection = this.mappingModalConnection();
      if (!modalConnection && this.mappingModalConnectionId() !== null) {
        this.mappingDialogRef?.close();
        this.mappingDialogRef = null;
        this.resetConnectionMappingDialogState();
      }
    });

    effect(() => {
      const modalNode = this.designTimeOverrideModalNode();
      if (!modalNode && this.designTimeOverrideModalNodeId() !== null) {
        this.designTimeOverrideDialogRef?.close();
        this.designTimeOverrideDialogRef = null;
        this.resetDesignTimeOverrideDialogState();
      }
    });

    void this.refreshBlueprints();
  }

  protected async save(): Promise<void> {
    const nodes = this.editorStore.nodes();
    const blueprintName = this.normalizedBlueprintName();

    this.blueprintNameTouched.set(true);
    if (nodes.length === 0 || !blueprintName) {
      return;
    }

    const connections = this.editorStore.connections();
    const destinationNodeIds = new Set(connections.map((connection) => connection.to.id));
    const rootNodes = nodes.filter((node) => !destinationNodeIds.has(node.id));
    const startingNodeId = rootNodes[0]?.id ?? nodes[0].id;
    let persistedDefinitionId = this.selectedBlueprintId();

    this.saving.set(true);
    try {
      if (persistedDefinitionId === null) {
        const created = await firstValueFrom(this.editor.createBlueprintGraph(
          blueprintName,
          startingNodeId,
          this.tasks(),
          nodes,
          connections,
        ));
        persistedDefinitionId = created.id;
        this.selectedBlueprintId.set(created.id);
      } else {
        await firstValueFrom(this.editor.createBlueprintVersion(
          persistedDefinitionId,
          this.selectedBlueprintVersion()?.version ?? this.selectedBlueprint()?.versions[0]?.version ?? null,
          startingNodeId,
          this.tasks(),
          nodes,
          connections,
        ));
      }

      await this.refreshBlueprints();
      if (persistedDefinitionId !== null) {
        const persistedBlueprint = this.blueprints().find((blueprint) => blueprint.id === persistedDefinitionId);
        this.selectedBlueprintId.set(persistedDefinitionId);
        this.selectedBlueprintVersionId.set(this.getPreferredBlueprintVersionId(persistedBlueprint, null));
        this.setBlueprintName(persistedBlueprint?.name ?? blueprintName);
      }

      this.pickerMode.set('selected');
      this.pickerReturnMode.set(null);
      this.blueprintSearch.setValue('');
      this.isEditingBlueprintName.set(false);
      this.blueprintNameTouched.set(false);
      this.scheduleUiTask(() => this.canvasViewport()?.focusCanvas());
    } finally {
      this.saving.set(false);
    }
  }

  protected async openBlueprint(blueprintId: number, blueprintVersionId?: number | null): Promise<void> {
    const graph = await firstValueFrom(this.editor.loadBlueprintGraph(blueprintId, this.tasks(), blueprintVersionId));
    this.editorStore.loadGraph(graph.nodes, graph.connections);
    this.selectedBlueprintId.set(graph.blueprint.id);
    this.selectedBlueprintVersionId.set(this.getPreferredBlueprintVersionId(graph.blueprint, blueprintVersionId ?? null));
    this.setBlueprintName(graph.blueprint.name);
    this.pickerMode.set('selected');
    this.pickerReturnMode.set(null);
    this.blueprintSearch.setValue('');
    this.isEditingBlueprintName.set(false);
    this.blueprintNameTouched.set(false);
    this.closeConnectionMappingModal();
    this.closeDesignTimeOverrideModal();
    this.scheduleUiTask(() => {
      this.canvasViewport()?.resetView();
      this.canvasViewport()?.focusCanvas();
    });
  }

  protected newBlueprint(): void {
    this.selectedBlueprintId.set(null);
    this.selectedBlueprintVersionId.set(null);
    this.editorStore.clearGraph();
    this.setDraftBlueprintName(DEFAULT_DRAFT_BLUEPRINT_NAME);
    this.pickerMode.set('draft');
    this.pickerReturnMode.set(null);
    this.blueprintSearch.setValue('');
    this.closeConnectionMappingModal();
    this.closeDesignTimeOverrideModal();
    this.scheduleUiTask(() => this.canvasViewport()?.focusCanvas());
  }

  protected beginBlueprintNameEdit(): void {
    if (this.isBlueprintNameReadonly() || this.saving() || !this.isDraftPickerMode()) {
      return;
    }

    this.blueprintNameDraft.set(this.committedBlueprintName());
    this.isEditingBlueprintName.set(true);
    this.pickerMode.set('draft-edit');
  }

  protected commitBlueprintNameEdit(): void {
    if (this.isBlueprintNameReadonly() || !this.isDraftPickerMode()) {
      return;
    }

    this.setBlueprintName(this.blueprintNameDraft());
    this.isEditingBlueprintName.set(false);
    this.pickerMode.set('draft');
    this.blueprintNameTouched.set(true);
  }

  protected cancelBlueprintNameEdit(): void {
    if (this.isBlueprintNameReadonly() || !this.isDraftPickerMode()) {
      return;
    }

    this.blueprintNameDraft.set(this.committedBlueprintName());
    this.isEditingBlueprintName.set(false);
    this.pickerMode.set('draft');
  }

  protected updateBlueprintNameDraft(value: string): void {
    this.blueprintNameDraft.set(value.slice(0, this.blueprintNameMaxLength));
  }

  protected onBlueprintNameKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      this.commitBlueprintNameEdit();
      return;
    }

    if (event.key === 'Escape') {
      event.preventDefault();
      this.cancelBlueprintNameEdit();
    }
  }

  protected beginPickerSearchFromTrigger(): void {
    if (this.pickerMode() !== 'selected') {
      return;
    }

    this.beginPickerSearch('selected');
  }

  protected beginPickerSearchFromDraftChevron(event?: Event): void {
    event?.stopPropagation();

    if (!this.isDraftPickerMode()) {
      this.beginPickerSearch(this.selectedBlueprintId() === null ? null : 'selected');
      return;
    }

    this.beginPickerSearch('draft');
  }

  protected onPickerSearchFocus(): void {
    if (this.pickerMode() !== 'search') {
      return;
    }

    this.blueprintPickerTrigger()?.openPanel();
  }

  protected onPickerSearchKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape') {
      return;
    }

    event.preventDefault();
    this.restorePickerAfterSearch();
  }

  protected onPickerAutocompleteOpened(): void {
    this.pickerPanelOpen.set(true);
  }

  protected onPickerAutocompleteClosed(): void {
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
    const blueprintId = Number(event.option.value);
    if (Number.isNaN(blueprintId)) {
      return;
    }

    this.pickerSelectionInProgress.set(true);
    void this.openBlueprint(blueprintId);
  }

  protected onBlueprintVersionSelected(blueprintVersionId: number | null): void {
    const blueprintId = this.selectedBlueprintId();
    if (blueprintId === null || blueprintVersionId === null || blueprintVersionId === this.selectedBlueprintVersionId()) {
      return;
    }

    void this.openBlueprint(blueprintId, blueprintVersionId);
  }

  protected formatBlueprintVersion(version: BlueprintVersionViewModel): string {
    return `Version ${version.version}`;
  }

  protected openDrawer(): void {
    this.drawerCollapsed.set(false);
  }

  protected closeDrawer(): void {
    this.drawerCollapsed.set(true);
  }

  protected addTaskFromDrawer(task: ITaskDefinition): void {
    if (!task.versions[0]?.id) {
      return;
    }

    const viewportCenter = this.canvasViewport()?.viewportCenterWorld() ?? { x: 420, y: 260 };
    const stagger = (this.editorStore.nodes().length % 5) * this.gridSize * 2;
    const node = this.editorStore.createNode(
      task,
      {
        x: viewportCenter.x + stagger,
        y: viewportCenter.y + stagger * 0.45,
      },
    );
    this.scheduleUiTask(() => {
      this.canvasViewport()?.centerOnNode(node.id);
      this.canvasViewport()?.focusCanvas();
    });

    if (this.compactDrawerViewport()) {
      this.closeDrawer();
    }
  }

  protected resetView(): void {
    this.canvasViewport()?.resetView();
  }

  protected zoomIn(): void {
    this.canvasViewport()?.zoomIn();
  }

  protected zoomOut(): void {
    this.canvasViewport()?.zoomOut();
  }

  protected undo(): void {
    this.editorStore.undo();
    this.canvasViewport()?.focusCanvas();
  }

  protected redo(): void {
    this.editorStore.redo();
    this.canvasViewport()?.focusCanvas();
  }

  protected copySelection(): void {
    this.editorStore.copySelection();
    this.canvasViewport()?.focusCanvas();
  }

  protected pasteSelection(): void {
    this.editorStore.pasteClipboard();
    this.canvasViewport()?.focusCanvas();
  }

  protected deleteSelection(): void {
    this.editorStore.deleteSelection();
    this.canvasViewport()?.focusCanvas();
  }

  protected openConnectionMappingModal(connectionId?: string): void {
    const targetConnectionId = connectionId ?? this.selectedConnection()?.id ?? null;
    if (!targetConnectionId) {
      return;
    }

    const connection = this.editorStore.connections().find((candidate) => candidate.id === targetConnectionId);
    if (!connection) {
      return;
    }

    this.mappingModalConnectionId.set(connection.id);
    this.mappingModalSelections.set(buildConnectionMappingSelections(connection));
    const template = this.mappingDialogTemplate();
    if (!template) {
      return;
    }

    this.mappingDialogRef?.close();
    const dialogRef = this.dialog.open(template, {
      width: 'min(42rem, calc(100vw - 2rem))',
      maxWidth: '100vw',
      autoFocus: false,
    });
    this.mappingDialogRef = dialogRef;
    dialogRef.afterClosed().subscribe(() => {
      if (this.mappingDialogRef === dialogRef) {
        this.mappingDialogRef = null;
        this.resetConnectionMappingDialogState();
      }
    });
  }

  protected closeConnectionMappingModal(): void {
    this.mappingDialogRef?.close();
    this.mappingDialogRef = null;
    this.resetConnectionMappingDialogState();
  }

  protected mappingModalSelectionFor(targetProperty: string): string {
    return this.mappingModalSelections()[targetProperty] ?? '';
  }

  protected updateMappingModalSelection(targetProperty: string, sourceProperty: string): void {
    this.mappingModalSelections.update((current) => ({
      ...current,
      [targetProperty]: sourceProperty,
    }));
  }

  protected mappingModalHasDirectOption(targetProperty: string): boolean {
    const connection = this.mappingModalConnection();
    if (!connection) {
      return false;
    }

    return Object.prototype.hasOwnProperty.call(
      connection.from.outputPropertiesForAnchor(connection.anchor),
      targetProperty,
    );
  }

  protected mappingModalAlternativeSourceProperties(targetProperty: string): string[] {
    return this.mappingModalSourceProperties().filter((sourceProperty) => sourceProperty !== targetProperty);
  }

  protected applyConnectionMappingModal(): void {
    const connection = this.mappingModalConnection();
    if (!connection) {
      return;
    }

    this.editorStore.updateConnectionPropertyMappings(
      connection.id,
      buildConnectionMappingsFromSelections(this.mappingModalSelections()),
    );
    this.closeConnectionMappingModal();
  }

  protected openDesignTimeOverrideModal(nodeId?: number): void {
    const targetNodeId = nodeId ?? this.selectedNode()?.id ?? null;
    if (targetNodeId === null) {
      return;
    }

    const node = this.editorStore.nodes().find((candidate) => candidate.id === targetNodeId);
    if (!node) {
      return;
    }

    this.designTimeOverrideModalNodeId.set(node.id);
    this.designTimeOverrideState.set({
      editorId: 'design-time',
      overrides: { ...node.designTimeInputOverrides },
      valid: true,
    });
    this.designTimeOverrideModalRevision.update((current) => current + 1);
    const template = this.designTimeOverrideDialogTemplate();
    if (!template) {
      return;
    }

    this.designTimeOverrideDialogRef?.close();
    const dialogRef = this.dialog.open(template, {
      width: 'min(48rem, calc(100vw - 2rem))',
      maxWidth: '100vw',
      autoFocus: false,
    });
    this.designTimeOverrideDialogRef = dialogRef;
    dialogRef.afterClosed().subscribe(() => {
      if (this.designTimeOverrideDialogRef === dialogRef) {
        this.designTimeOverrideDialogRef = null;
        this.resetDesignTimeOverrideDialogState();
      }
    });
  }

  protected closeDesignTimeOverrideModal(): void {
    this.designTimeOverrideDialogRef?.close();
    this.designTimeOverrideDialogRef = null;
    this.resetDesignTimeOverrideDialogState();
  }

  protected clearDesignTimeOverrides(): void {
    this.designTimeOverrideState.set({
      editorId: 'design-time',
      overrides: {},
      valid: true,
    });

    const node = this.designTimeOverrideModalNode();
    if (!node) {
      return;
    }

    this.editorStore.updateNodeDesignTimeOverrides(node.id, {});
    this.closeDesignTimeOverrideModal();
  }

  protected onDesignTimeOverrideStateChanged(state: PropertyOverrideEditorState): void {
    this.designTimeOverrideState.set(state);
  }

  protected applyDesignTimeOverrides(): void {
    const node = this.designTimeOverrideModalNode();
    if (!node || !this.designTimeOverrideState().valid) {
      return;
    }

    this.editorStore.updateNodeDesignTimeOverrides(node.id, this.designTimeOverrideState().overrides);
    this.closeDesignTimeOverrideModal();
  }

  protected formatPropertyLabel(value: string): string {
    return value
      .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
      .replace(/[_-]+/g, ' ')
      .replace(/^./, (firstCharacter) => firstCharacter.toUpperCase());
  }

  protected blueprintHasUpgradeWarning(blueprint: BlueprintViewModel): boolean {
    const latestVersion = blueprint.versions[0];
    if (!latestVersion) {
      return false;
    }

    return latestVersion.tasks.some((task) => {
      const taskDefinition = this.tasks().find((candidate) => candidate.id === task.taskDefinitionId);
      return taskDefinition
        ? this.editor.hasNewerMajorMinorLane(taskDefinition, task.allowedMajor, task.allowedMinor)
        : false;
    });
  }

  private async refreshBlueprints(): Promise<void> {
    const blueprints = await firstValueFrom(this.editor.getBlueprints());
    this.blueprints.set(blueprints);

    const selectedBlueprintId = this.selectedBlueprintId();
    if (selectedBlueprintId === null) {
      return;
    }

    const selectedBlueprint = blueprints.find((blueprint) => blueprint.id === selectedBlueprintId) ?? null;
    this.selectedBlueprintVersionId.set(this.getPreferredBlueprintVersionId(
      selectedBlueprint,
      this.selectedBlueprintVersionId(),
    ));
  }

  private setBlueprintName(name: string): void {
    const normalizedName = name.trim().slice(0, this.blueprintNameMaxLength);
    this.committedBlueprintName.set(normalizedName);
    this.blueprintNameDraft.set(normalizedName);
  }

  private setDraftBlueprintName(name: string): void {
    this.setBlueprintName(name);
    this.isEditingBlueprintName.set(false);
    this.blueprintNameTouched.set(false);
  }

  private clearBlueprintName(): void {
    this.committedBlueprintName.set('');
    this.blueprintNameDraft.set('');
    this.isEditingBlueprintName.set(false);
    this.blueprintNameTouched.set(false);
  }

  private getPreferredBlueprintVersionId(
    blueprint: BlueprintViewModel | null | undefined,
    preferredBlueprintVersionId: number | null,
  ): number | null {
    if (!blueprint) {
      return null;
    }

    if (preferredBlueprintVersionId !== null) {
      const preferredVersion = blueprint.versions.find((version) => version.blueprintVersionId === preferredBlueprintVersionId);
      if (preferredVersion) {
        return preferredVersion.blueprintVersionId;
      }
    }

    return blueprint.versions[0]?.blueprintVersionId ?? null;
  }

  private beginPickerSearch(returnMode: BlueprintPickerReturnMode): void {
    this.pickerReturnMode.set(returnMode);
    this.pickerFocusRequested.set(true);
    this.pickerMode.set('search');
    this.blueprintSearch.setValue('');
  }

  private restorePickerAfterSearch(): void {
    const returnMode = untracked(() => this.pickerReturnMode());
    this.pickerFocusRequested.set(false);
    this.blueprintSearch.setValue('');
    this.pickerReturnMode.set(null);

    if (returnMode !== null) {
      this.pickerMode.set(returnMode);
      return;
    }

    if (this.selectedBlueprintId() !== null) {
      this.pickerMode.set('selected');
      return;
    }

    if (this.committedBlueprintName().trim().length > 0) {
      this.pickerMode.set('draft');
      return;
    }

    this.pickerMode.set('search');
  }

  private isCompactDrawerViewport(): boolean {
    return typeof window !== 'undefined' && window.matchMedia('(max-width: 1180px)').matches;
  }

  private scheduleUiTask(task: () => void): void {
    if (typeof requestAnimationFrame === 'function') {
      requestAnimationFrame(task);
      return;
    }

    queueMicrotask(task);
  }

  private resetConnectionMappingDialogState(): void {
    this.mappingModalConnectionId.set(null);
    this.mappingModalSelections.set({});
  }

  private resetDesignTimeOverrideDialogState(): void {
    this.designTimeOverrideModalNodeId.set(null);
  }

  private buildConnectionBehaviorChips(summary: ConnectionBehaviorSummary | null): SummaryChip[] {
    if (!summary) {
      return [];
    }

    const chips: SummaryChip[] = [];
    if (summary.isMappedOrPartial) {
      chips.push({
        icon: 'swap_horiz',
        label: summary.behaviorLabel,
        tone: 'mapped',
      });
    } else {
      chips.push({
        icon: 'south_east',
        label: summary.behaviorLabel,
        tone: 'default',
      });
    }

    if (summary.isFanOut) {
      chips.push({
        icon: 'alt_route',
        label: `${summary.siblingCount} downstream nodes`,
        tone: 'fanOut',
      });
    }

    return chips;
  }

  private filterBlueprints(
    blueprints: BlueprintViewModel[],
    query: string,
  ): BlueprintViewModel[] {
    const trimmedQuery = query.trim();
    if (!trimmedQuery) {
      return [...blueprints].sort((a, b) => a.name.localeCompare(b.name));
    }

    return blueprints
      .map((blueprint) => ({
        blueprint,
        match: this.getBlueprintMatch(blueprint.name, trimmedQuery),
      }))
      .filter((item): item is { blueprint: BlueprintViewModel; match: { rank: number; score: number } } => item.match !== null)
      .sort((a, b) =>
        a.match.rank - b.match.rank ||
        a.match.score - b.match.score ||
        a.blueprint.name.localeCompare(b.blueprint.name),
      )
      .map((item) => item.blueprint);
  }

  private getBlueprintMatch(
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

    const fuzzyScore = this.getFuzzyMatchScore(normalizedName, normalizedQuery);
    if (fuzzyScore !== null) {
      return { rank: 3, score: fuzzyScore };
    }

    return null;
  }

  private getFuzzyMatchScore(text: string, query: string): number | null {
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

  private buildTaskLibraryItems(tasks: ITaskDefinition[]): TaskLibraryItem[] {
    return [...tasks]
      .map((task) => {
        const category = this.getTaskCategory(task);
        const description = task.versions[0]?.description?.trim() || 'Available task';

        return {
          task,
          icon: this.getTaskIcon(category.label, task),
          categoryLabel: category.label,
          description,
          tone: category.tone,
          searchText: `${task.name} ${category.label} ${description}`.toLocaleLowerCase(),
        };
      })
      .sort((left, right) => left.task.name.localeCompare(right.task.name));
  }

  private getTaskCategory(task: ITaskDefinition): {
    label: 'Trigger' | 'Logic' | 'Action';
    tone: 'trigger' | 'logic' | 'action';
  } {
    const version = task.versions[0];
    if (!version) {
      return {
        label: 'Action',
        tone: 'action',
      };
    }

    if (!version.inputChannel) {
      return {
        label: 'Trigger',
        tone: 'trigger',
      };
    }

    if (version.outputChannels.length > 1) {
      return {
        label: 'Logic',
        tone: 'logic',
      };
    }

    return {
      label: 'Action',
      tone: 'action',
    };
  }

  private filterTaskLibraryItems(
    items: TaskLibraryItem[],
    query: string,
  ): TaskLibraryItem[] {
    const trimmedQuery = query.trim().toLocaleLowerCase();
    if (!trimmedQuery) {
      return items;
    }

    return items.filter((item) => item.searchText.includes(trimmedQuery));
  }

  private getTaskIcon(category: string, task: ITaskDefinition): string {
    const normalizedName = task.name.toLocaleLowerCase();

    if (normalizedName.includes('email') || normalizedName.includes('mail')) return 'mail';
    if (normalizedName.includes('schedule') || normalizedName.includes('timer')) return 'schedule';
    if (normalizedName.includes('webhook') || normalizedName.includes('http')) return 'bolt';
    if (normalizedName.includes('database') || normalizedName.includes('sql')) return 'storage';
    if (normalizedName.includes('condition') || normalizedName.includes('switch')) return 'account_tree';
    if (category === 'Trigger') return 'play_arrow';
    if (category === 'Logic') return 'alt_route';
    return 'extension';
  }

  @HostListener('window:resize')
  protected onWindowResize(): void {
    this.compactDrawerViewport.set(this.isCompactDrawerViewport());
  }
}
