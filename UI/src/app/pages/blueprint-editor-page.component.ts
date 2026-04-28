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
  viewChildren,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import {
  MatAutocompleteModule,
  MatAutocompleteSelectedEvent,
  MatAutocompleteTrigger,
} from '@angular/material/autocomplete';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { firstValueFrom } from 'rxjs';
import {
  BlueprintEditorService,
  BlueprintVersionViewModel,
  BlueprintViewModel,
} from '../domain/blueprint-editor.service';
import {
  BlueprintEditorStore,
  NodeVersionChangeImpact,
} from '../domain/blueprint-editor.store';
import { BuilderInputService } from '../domain/builder-input.service';
import { TaskDefinitionsService } from '../domain/task-definitions.service';
import { ITaskDefinition, ITaskVersion } from '../interfaces/ITaskDefinition';
import { BlueprintCanvasComponent } from '../components/blueprint-canvas.component';
import type {
  CanvasValidationFocus,
  CanvasValidationIssueMarker,
  CanvasViewportInsets,
} from '../components/blueprint-canvas.component';
import { NodeModel } from '../domain/node-model';
import type { Side } from '../domain/node-model';
import { DynamicFieldDefinition, DynamicFormFieldComponent } from '../components/dynamic-form-field.component';
import {
  createTaskPropertyControl,
  createTaskPropertyFieldDefinitions,
  serializeTaskPropertyControlValue,
  taskPropertyKindLabel,
} from '../domain/task-property-form-utils';
import { getLatestPatchPerMinorVersion, getLatestTaskVersion, TaskVersionLaneOption } from '../domain/task-version-utils';
import {
  BlueprintValidationIssue,
  validateBlueprintGraph,
} from '../domain/blueprint-validation';

interface TaskLibraryItem {
  task: ITaskDefinition;
  displayName: string;
  icon: string;
  categoryLabel: string;
  description: string;
  tone: 'trigger' | 'logic' | 'action';
  searchText: string;
}

type BlueprintPickerMode = 'search' | 'selected' | 'draft' | 'draft-edit';
type BlueprintPickerReturnMode = 'selected' | 'draft' | null;
type BuilderDrawerContent = 'plugin-library' | 'node-settings';
type ValidationBadgeTone = 'error' | 'warning' | 'valid';

interface BlueprintPickerSuggestion {
  blueprint: BlueprintViewModel;
  isActive: boolean;
}

type BlueprintPickerOption = BlueprintPickerBlueprintOption | BlueprintPickerVersionOption;

interface BlueprintPickerBlueprintOption {
  kind: 'blueprint';
  value: string;
  blueprint: BlueprintViewModel;
  isActive: boolean;
}

interface BlueprintPickerVersionOption {
  kind: 'version';
  value: string;
  blueprint: BlueprintViewModel;
  version: BlueprintVersionViewModel;
  isSelected: boolean;
}

type ParsedBlueprintPickerOption =
  | { kind: 'blueprint'; blueprintId: number }
  | { kind: 'version'; blueprintId: number; blueprintVersionId: number };

interface PendingNodeVersionChange {
  nodeId: number;
  version: ITaskVersion;
  impact: NodeVersionChangeImpact;
}

const BLUEPRINT_NAME_MAX_LENGTH = 100;
const DEFAULT_DRAFT_BLUEPRINT_NAME = 'Draft Blueprint';
const NODE_UPGRADE_WARNING_MESSAGE = 'A newer task version is available for this node.';

@Component({
  selector: 'app-blueprint-editor-page',
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatCardModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatMenuModule,
    MatSelectModule,
    MatTooltipModule,
    BlueprintCanvasComponent,
    DynamicFormFieldComponent,
  ],
  templateUrl: './blueprint-editor-page.component.html',
  styleUrl: './blueprint-editor-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [BlueprintEditorStore, BuilderInputService],
})
export class BlueprintEditorPageComponent {
  protected readonly Math = Math;
  protected readonly canvasBackgroundEffect = 'stitch' as const;
  protected readonly nodeUpgradeWarningMessage = NODE_UPGRADE_WARNING_MESSAGE;

  protected readonly editor = inject(BlueprintEditorService);
  protected readonly editorStore = inject(BlueprintEditorStore);
  private readonly dialog = inject(MatDialog);
  private readonly taskDefinitions = inject(TaskDefinitionsService);
  private readonly builderInput = inject(BuilderInputService);
  private readonly canvasViewport = viewChild(BlueprintCanvasComponent);
  private readonly validationMenuTriggers = viewChildren<MatMenuTrigger>('validationMenuTrigger');
  private readonly blueprintPickerInput = viewChild<ElementRef<HTMLInputElement>>('blueprintPickerInput');
  private readonly blueprintPickerTrigger = viewChild(MatAutocompleteTrigger);
  private readonly blueprintNameInput = viewChild<ElementRef<HTMLInputElement>>('blueprintNameInput');
  private readonly nodeVersionChangeDialogTemplate = viewChild<TemplateRef<unknown>>('nodeVersionChangeDialogTemplate');
  private readonly gridSize = 20;
  private nodeVersionChangeDialogRef: MatDialogRef<unknown> | null = null;
  private syncingStaticOverrideForm = false;
  private staticOverrideFormKey = '';

  protected readonly blueprintNameMaxLength = BLUEPRINT_NAME_MAX_LENGTH;
  protected readonly saving = signal(false);
  protected readonly saveHovered = signal(false);
  protected readonly validationErrorPulse = signal(false);
  protected readonly focusedValidationIssueId = signal<string | null>(null);
  protected readonly validationFocusPulseKey = signal(0);
  protected readonly blueprints = signal<BlueprintViewModel[]>([]);
  protected readonly selectedBlueprintId = signal<number | null>(null);
  protected readonly selectedBlueprintVersionId = signal<number | null>(null);
  protected readonly activeDrawer = signal<BuilderDrawerContent | null>(null);
  protected readonly drawerCollapsed = computed(() => this.activeDrawer() === null);
  protected readonly compactDrawerViewport = signal(this.isCompactDrawerViewport());
  protected readonly pickerMode = signal<BlueprintPickerMode>('search');
  protected readonly pickerPanelOpen = signal(false);
  protected readonly committedBlueprintName = signal('');
  protected readonly blueprintNameDraft = signal('');
  protected readonly isEditingBlueprintName = signal(false);
  protected readonly blueprintNameTouched = signal(false);
  protected readonly nodeSettingsNodeId = signal<number | null>(null);
  protected readonly staticOverrideDraftKeys = signal<string[]>([]);
  protected readonly staticOverrideFields = signal<DynamicFieldDefinition[]>([]);
  protected readonly pendingNodeVersionChange = signal<PendingNodeVersionChange | null>(null);
  protected readonly staticOverrideForm = new FormGroup({});
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
  protected readonly canvasViewportInsets = computed<CanvasViewportInsets>(() => ({
    top: 96,
    right: this.drawerCollapsed()
      ? 120
      : this.compactDrawerViewport()
        ? 120
        : 560,
    bottom: 96,
    left: 24,
  }));
  private readonly canvasRouteValidationErrors = computed(() => this.canvasViewport()?.routeValidationErrors() ?? []);
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
    this.sortBlueprintVersions(this.selectedBlueprint()?.versions ?? []),
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
  protected readonly validationSummary = computed(() =>
    validateBlueprintGraph({
      blueprintName: this.normalizedBlueprintName(),
      nodes: this.editorStore.nodes(),
      connections: this.editorStore.connections(),
      routeValidationErrors: this.canvasRouteValidationErrors(),
    }),
  );
  protected readonly validationErrors = computed(() => this.validationSummary().errors);
  protected readonly validationWarnings = computed(() => this.validationSummary().warnings);
  protected readonly canvasIssueMarkers = computed<readonly CanvasValidationIssueMarker[]>(() => {
    const validationMarkers = [
      ...this.validationErrors(),
      ...this.validationWarnings(),
    ]
      .filter((issue) => issue.nodeId !== undefined)
      .map((issue) => ({
        issueId: issue.id,
        severity: issue.severity,
        nodeId: issue.nodeId!,
        message: issue.message,
        propertyName: issue.propertyName ?? null,
        channelName: issue.channelName ?? null,
        side: this.validationIssueSide(issue),
      }));

    const upgradeMarkers = this.editorStore.nodes()
      .filter((node) => node.hasUpgradeWarning)
      .map((node) => ({
        issueId: `node-upgrade:${node.id}`,
        severity: 'warning' as const,
        nodeId: node.id,
        message: NODE_UPGRADE_WARNING_MESSAGE,
        propertyName: null,
        channelName: null,
        side: null,
      }));

    return [...validationMarkers, ...upgradeMarkers];
  });
  protected readonly validationCanvasFocus = computed<CanvasValidationFocus | null>(() => {
    const focusedIssueId = this.focusedValidationIssueId();
    if (!focusedIssueId) {
      return null;
    }

    const issue = this.findValidationIssue(focusedIssueId);
    return issue && this.isIssueCanvasTargetable(issue)
      ? this.createCanvasValidationFocus(issue, this.validationFocusPulseKey())
      : null;
  });
  protected readonly validationBadgeGlow = computed(() =>
    this.saveHovered() &&
    this.validationSummary().errorCount === 0 &&
    this.validationSummary().warningCount > 0,
  );
  protected readonly validationBadgeTone = computed<ValidationBadgeTone>(() => {
    const summary = this.validationSummary();
    if (summary.errorCount > 0) {
      return 'error';
    }

    if (summary.warningCount > 0) {
      return 'warning';
    }

    return 'valid';
  });
  protected readonly validationBadgeIcon = computed(() => {
    const tone = this.validationBadgeTone();
    if (tone === 'error') {
      return 'error';
    }

    if (tone === 'warning') {
      return 'warning';
    }

    return 'check_circle';
  });
  protected readonly validationBadgeLabel = computed(() => {
    const tone = this.validationBadgeTone();
    if (tone === 'error') {
      return 'Blocked';
    }

    if (tone === 'warning') {
      return 'Warnings';
    }

    return 'No issues';
  });
  protected readonly validationBadgeIssueCount = computed(() => {
    const summary = this.validationSummary();
    return summary.errorCount > 0 ? summary.errorCount : summary.warningCount;
  });
  protected readonly validationBadgeAriaLabel = computed(() => {
    const summary = this.validationSummary();
    if (summary.errorCount > 0) {
      return `Review ${summary.errorCount} blueprint validation ${summary.errorCount === 1 ? 'error' : 'errors'}`;
    }

    if (summary.warningCount > 0) {
      return `Review ${summary.warningCount} blueprint validation ${summary.warningCount === 1 ? 'warning' : 'warnings'}`;
    }

    return 'Blueprint has no validation issues';
  });
  protected readonly nodeSettingsNode = computed<NodeModel | null>(() =>
    this.editorStore.nodes().find((node) => node.id === this.nodeSettingsNodeId()) ?? null,
  );
  protected readonly nodeSettingsTaskDefinition = computed<ITaskDefinition | null>(() => {
    const node = this.nodeSettingsNode();
    return node ? this.tasks().find((task) => task.id === node.taskDefinitionId) ?? null : null;
  });
  protected readonly nodeSettingsValidationIssues = computed<readonly BlueprintValidationIssue[]>(() => {
    const node = this.nodeSettingsNode();
    if (!node) {
      return [];
    }

    return [
      ...this.validationErrors(),
      ...this.validationWarnings(),
    ].filter((issue) => issue.nodeId === node.id);
  });
  protected readonly nodeSettingsHasUpgradeWarning = computed(() =>
    this.nodeSettingsNode()?.hasUpgradeWarning ?? false,
  );
  protected readonly nodeSettingsIssueSummaryLabel = computed(() => {
    const issues = this.nodeSettingsValidationIssues();
    const errorCount = issues.filter((issue) => issue.severity === 'error').length;
    const warningCount = issues.filter((issue) => issue.severity === 'warning').length +
      (this.nodeSettingsHasUpgradeWarning() ? 1 : 0);
    const parts: string[] = [];
    if (errorCount > 0) {
      parts.push(`${errorCount} ${errorCount === 1 ? 'error' : 'errors'}`);
    }
    if (warningCount > 0) {
      parts.push(`${warningCount} ${warningCount === 1 ? 'warning' : 'warnings'}`);
    }

    return parts.length > 0
      ? parts.join(' · ')
      : 'No node issues';
  });
  protected readonly nodeVersionOptions = computed<TaskVersionLaneOption[]>(() =>
    getLatestPatchPerMinorVersion(this.nodeSettingsTaskDefinition()?.versions ?? []),
  );
  protected readonly selectedNodeVersion = computed<ITaskVersion | null>(() => {
    const node = this.nodeSettingsNode();
    const task = this.nodeSettingsTaskDefinition();
    if (!node || !task) {
      return null;
    }

    return task.versions.find((version) => version.id === node.resolvedVersionId) ?? null;
  });
  protected readonly availableStaticOverrideProperties = computed(() => {
    const node = this.nodeSettingsNode();
    if (!node) {
      return [];
    }

    const draftKeys = new Set(this.staticOverrideDraftKeys());
    return [...node.inputPropertyDefinitions]
      .filter((property) => !draftKeys.has(property.name))
      .sort((left, right) => left.name.localeCompare(right.name));
  });
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

    const baseLabel = this.isBlueprintNameReadonly() ? 'Save New Version' : 'Create Blueprint';
    const validation = this.validationSummary();
    if (validation.errorCount > 0) {
      return `${validation.errorCount} validation ${validation.errorCount === 1 ? 'error' : 'errors'} prevent saving.`;
    }

    if (validation.warningCount > 0) {
      return `${baseLabel} with ${validation.warningCount} ${validation.warningCount === 1 ? 'warning' : 'warnings'}.`;
    }

    return baseLabel;
  });
  protected readonly canSave = computed(() =>
    !this.saving() &&
    this.validationSummary().canSave,
  );
  protected readonly pluginLibraryDrawerMode = computed<'overlay' | 'overlay-with-backdrop'>(() =>
    this.compactDrawerViewport() ? 'overlay-with-backdrop' : 'overlay',
  );
  protected readonly pluginLibraryHasBackdrop = computed(() =>
    this.pluginLibraryDrawerMode() === 'overlay-with-backdrop',
  );
  protected readonly isPluginLibraryDrawerOpen = computed(() => this.activeDrawer() === 'plugin-library');
  protected readonly isNodeSettingsDrawerOpen = computed(() => this.activeDrawer() === 'node-settings');
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
  protected readonly pickerOptions = computed<BlueprintPickerOption[]>(() => {
    const selectedBlueprintVersionId = this.selectedBlueprintVersionId();
    const options: BlueprintPickerOption[] = [];

    for (const suggestion of this.pickerSuggestions()) {
      options.push({
        kind: 'blueprint',
        value: this.createBlueprintPickerOptionValue(suggestion.blueprint.id),
        blueprint: suggestion.blueprint,
        isActive: suggestion.isActive,
      });

      if (!suggestion.isActive) {
        continue;
      }

      for (const version of this.sortBlueprintVersions(suggestion.blueprint.versions)) {
        options.push({
          kind: 'version',
          value: this.createBlueprintVersionPickerOptionValue(suggestion.blueprint.id, version.blueprintVersionId),
          blueprint: suggestion.blueprint,
          version,
          isSelected: version.blueprintVersionId === selectedBlueprintVersionId,
        });
      }
    }

    return options;
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
      const focusedIssueId = this.focusedValidationIssueId();
      if (!focusedIssueId) {
        return;
      }

      const issue = this.findValidationIssue(focusedIssueId);
      if (!issue || !this.isIssueCanvasTargetable(issue)) {
        this.focusedValidationIssueId.set(null);
      }
    });

    effect(() => {
      const node = this.nodeSettingsNode();
      if (!node && this.nodeSettingsNodeId() !== null) {
        this.closeDrawer();
        return;
      }

      if (node) {
        this.syncStaticOverrideForm(node);
      }
    });

    this.staticOverrideForm.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.persistStaticOverridesFromForm());

    void this.refreshBlueprints();
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }

    const nodes = this.editorStore.nodes();
    const blueprintName = this.normalizedBlueprintName();

    this.blueprintNameTouched.set(true);
    if (!this.validationSummary().canSave) {
      this.triggerValidationErrorPulse();
      return;
    }

    const connections = this.editorStore.connections();
    const destinationNodeIds = new Set(connections.map((connection) => connection.to.id));
    const rootNodes = nodes.filter((node) => !destinationNodeIds.has(node.id));
    const startingNodeId = rootNodes[0]?.id ?? nodes[0]!.id;
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
    this.closeDrawer();
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
    this.closeDrawer();
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

  protected beginPickerSearchFromVersionDisplay(event: Event): void {
    event.stopPropagation();

    if (this.pickerMode() !== 'selected') {
      return;
    }

    this.beginPickerSearch('selected');
  }

  protected onPickerShellClick(): void {
    const mode = this.pickerMode();
    if (mode === 'search' || mode === 'draft-edit') {
      return;
    }

    this.beginPickerSearch(mode === 'draft' ? 'draft' : 'selected');
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
    const parsedOption = this.parseBlueprintPickerOptionValue(event.option.value);
    if (!parsedOption) {
      this.restorePickerAfterSearch();
      return;
    }

    if (parsedOption.kind === 'version') {
      if (
        parsedOption.blueprintId === this.selectedBlueprintId() &&
        parsedOption.blueprintVersionId === this.selectedBlueprintVersionId()
      ) {
        this.restorePickerAfterSearch();
        return;
      }

      this.pickerSelectionInProgress.set(true);
      void this.openBlueprint(parsedOption.blueprintId, parsedOption.blueprintVersionId);
      return;
    }

    if (parsedOption.blueprintId === this.selectedBlueprintId()) {
      this.restorePickerAfterSearch();
      return;
    }

    this.pickerSelectionInProgress.set(true);
    void this.openBlueprint(parsedOption.blueprintId);
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

  protected formatBlueprintVersionCreatedDate(version: BlueprintVersionViewModel): string | null {
    if (!version.createdAt) {
      return null;
    }

    const createdAt = new Date(version.createdAt);
    if (Number.isNaN(createdAt.getTime())) {
      return null;
    }

    const day = String(createdAt.getDate()).padStart(2, '0');
    const month = String(createdAt.getMonth() + 1).padStart(2, '0');
    return `${day}/${month}/${createdAt.getFullYear()}`;
  }

  protected openDrawer(): void {
    this.nodeSettingsNodeId.set(null);
    this.activeDrawer.set('plugin-library');
  }

  protected closeDrawer(): void {
    this.activeDrawer.set(null);
    this.nodeSettingsNodeId.set(null);
    this.staticOverrideDraftKeys.set([]);
    this.staticOverrideFields.set([]);
    this.staticOverrideFormKey = '';
  }

  protected openNodeSettings(nodeId: number): void {
    const node = this.editorStore.nodes().find((candidate) => candidate.id === nodeId);
    if (!node) {
      return;
    }

    this.nodeSettingsNodeId.set(node.id);
    this.editorStore.selectOnlyNode(node.id);
    this.activeDrawer.set('node-settings');
    this.syncStaticOverrideForm(node);
  }

  protected addTaskFromDrawer(task: ITaskDefinition): void {
    if (task.versions.length === 0) {
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

  protected onSaveHover(value: boolean): void {
    this.saveHovered.set(value);
  }

  protected validationStatusTooltip(): string {
    const validation = this.validationSummary();
    if (validation.errorCount > 0) {
      return `${validation.errorCount} validation ${validation.errorCount === 1 ? 'error' : 'errors'} must be fixed before saving.`;
    }

    if (validation.warningCount > 0) {
      return 'Blueprint can be saved, but warnings should be reviewed.';
    }

    return 'Blueprint is ready to save.';
  }

  protected issueTrackById(issue: BlueprintValidationIssue): string {
    return issue.id;
  }

  protected isIssueCanvasTargetable(issue: BlueprintValidationIssue): boolean {
    return issue.nodeId !== undefined || issue.connectionId !== undefined;
  }

  protected focusValidationIssue(issue: BlueprintValidationIssue): void {
    if (!this.isIssueCanvasTargetable(issue)) {
      return;
    }

    const nextPulseKey = this.validationFocusPulseKey() + 1;
    const focus = this.createCanvasValidationFocus(issue, nextPulseKey);
    this.focusedValidationIssueId.set(issue.id);
    this.validationFocusPulseKey.set(nextPulseKey);
    for (const trigger of this.validationMenuTriggers()) {
      trigger.closeMenu();
    }

    this.scheduleUiTask(() => this.canvasViewport()?.revealValidationFocus(focus));
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

  protected onNodeVersionSelected(versionId: number | null): void {
    const node = this.nodeSettingsNode();
    const task = this.nodeSettingsTaskDefinition();
    if (!node || !task || versionId === null || versionId === node.resolvedVersionId) {
      return;
    }

    const version = task.versions.find((candidate) => candidate.id === versionId);
    if (!version) {
      return;
    }

    const impact = this.editorStore.analyzeNodeVersionChange(node.id, version);
    if (!impact.hasDestructiveChanges) {
      this.applyNodeVersionChange(node.id, version);
      return;
    }

    this.pendingNodeVersionChange.set({ nodeId: node.id, version, impact });
    const template = this.nodeVersionChangeDialogTemplate();
    if (!template) {
      return;
    }

    this.nodeVersionChangeDialogRef?.close();
    const dialogRef = this.dialog.open(template, {
      width: 'min(32rem, calc(100vw - 2rem))',
      maxWidth: '100vw',
      autoFocus: false,
    });
    this.nodeVersionChangeDialogRef = dialogRef;
    dialogRef.afterClosed().subscribe(() => {
      if (this.nodeVersionChangeDialogRef === dialogRef) {
        this.nodeVersionChangeDialogRef = null;
        this.pendingNodeVersionChange.set(null);
      }
    });
  }

  protected confirmNodeVersionChange(): void {
    const pendingChange = this.pendingNodeVersionChange();
    if (!pendingChange) {
      return;
    }

    this.applyNodeVersionChange(pendingChange.nodeId, pendingChange.version);
    this.nodeVersionChangeDialogRef?.close();
    this.nodeVersionChangeDialogRef = null;
    this.pendingNodeVersionChange.set(null);
  }

  protected cancelNodeVersionChange(): void {
    this.nodeVersionChangeDialogRef?.close();
    this.nodeVersionChangeDialogRef = null;
    this.pendingNodeVersionChange.set(null);
  }

  protected addStaticOverride(propertyName: string): void {
    const node = this.nodeSettingsNode();
    if (!node || !node.inputPropertyDefinitions.some((property) => property.name === propertyName)) {
      return;
    }

    this.staticOverrideDraftKeys.update((keys) =>
      keys.includes(propertyName)
        ? keys
        : [...keys, propertyName].sort((left, right) => left.localeCompare(right)),
    );
    this.syncStaticOverrideForm(node, true);
  }

  protected removeStaticOverride(propertyName: string): void {
    const node = this.nodeSettingsNode();
    if (!node) {
      return;
    }

    this.staticOverrideDraftKeys.update((keys) => keys.filter((key) => key !== propertyName));
    const nextOverrides = { ...node.designTimeInputOverrides };
    delete nextOverrides[propertyName];
    this.editorStore.updateNodeDesignTimeOverrides(node.id, nextOverrides);
    const refreshedNode = this.editorStore.nodes().find((candidate) => candidate.id === node.id) ?? node;
    this.syncStaticOverrideForm(refreshedNode, true);
  }

  protected staticOverrideKindLabel(field: DynamicFieldDefinition): string {
    return taskPropertyKindLabel(field.kind);
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

  private findValidationIssue(issueId: string): BlueprintValidationIssue | null {
    return [
      ...this.validationSummary().errors,
      ...this.validationSummary().warnings,
    ].find((issue) => issue.id === issueId) ?? null;
  }

  private createCanvasValidationFocus(issue: BlueprintValidationIssue, pulseKey: number): CanvasValidationFocus {
    return {
      issueId: issue.id,
      severity: issue.severity,
      nodeId: issue.nodeId ?? null,
      connectionId: issue.connectionId ?? null,
      routeIndex: issue.routeIndex ?? null,
      propertyName: issue.propertyName ?? null,
      channelName: issue.channelName ?? null,
      side: this.validationIssueSide(issue),
      pulseKey,
    };
  }

  private validationIssueSide(issue: BlueprintValidationIssue): Side | null {
    switch (issue.code) {
      case 'node.input.requiredMissing':
      case 'connection.mapping.targetMissing':
        return 'left';
      case 'connection.mapping.sourceMissing':
      case 'node.output.propertyUnused':
      case 'node.output.channelUnmapped':
        return 'right';
      default:
        return null;
    }
  }

  private sortBlueprintVersions(versions: readonly BlueprintVersionViewModel[]): BlueprintVersionViewModel[] {
    return [...versions].sort((left, right) => right.version - left.version);
  }

  private createBlueprintPickerOptionValue(blueprintId: number): string {
    return `blueprint:${blueprintId}`;
  }

  private createBlueprintVersionPickerOptionValue(blueprintId: number, blueprintVersionId: number): string {
    return `blueprint:${blueprintId}:version:${blueprintVersionId}`;
  }

  private parseBlueprintPickerOptionValue(value: unknown): ParsedBlueprintPickerOption | null {
    if (typeof value === 'number' && Number.isFinite(value)) {
      return {
        kind: 'blueprint',
        blueprintId: value,
      };
    }

    if (typeof value !== 'string') {
      return null;
    }

    const blueprintMatch = /^blueprint:(\d+)$/.exec(value);
    if (blueprintMatch) {
      return {
        kind: 'blueprint',
        blueprintId: Number(blueprintMatch[1]),
      };
    }

    const versionMatch = /^blueprint:(\d+):version:(\d+)$/.exec(value);
    if (versionMatch) {
      return {
        kind: 'version',
        blueprintId: Number(versionMatch[1]),
        blueprintVersionId: Number(versionMatch[2]),
      };
    }

    return null;
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

  private triggerValidationErrorPulse(): void {
    this.validationErrorPulse.set(false);
    this.scheduleUiTask(() => this.validationErrorPulse.set(true));
  }

  private applyNodeVersionChange(nodeId: number, version: ITaskVersion): void {
    this.editorStore.updateNodeTaskVersion(nodeId, version);
    const node = this.editorStore.nodes().find((candidate) => candidate.id === nodeId) ?? null;
    if (node) {
      this.syncStaticOverrideForm(node, true);
    }
  }

  private syncStaticOverrideForm(node: NodeModel, force = false): void {
    const inputFieldDefinitions = createTaskPropertyFieldDefinitions(node.inputPropertyDefinitions, false);
    const fieldsByKey = new Map(inputFieldDefinitions.map((field) => [field.key, field] as const));
    const overrideKeys = Object.keys(node.designTimeInputOverrides)
      .filter((propertyName) => fieldsByKey.has(propertyName))
      .sort((left, right) => left.localeCompare(right));
    const draftKeys = [...new Set([...overrideKeys, ...this.staticOverrideDraftKeys()])]
      .filter((propertyName) => fieldsByKey.has(propertyName))
      .sort((left, right) => left.localeCompare(right));
    const formKey = `${node.id}:${node.resolvedVersionId}:${draftKeys.join('|')}`;

    if (!force && formKey === this.staticOverrideFormKey) {
      return;
    }

    this.staticOverrideFormKey = formKey;
    this.syncingStaticOverrideForm = true;
    for (const controlName of Object.keys(this.staticOverrideForm.controls)) {
      this.staticOverrideForm.removeControl(controlName, { emitEvent: false });
    }

    const fields = draftKeys
      .map((propertyName) => fieldsByKey.get(propertyName))
      .filter((field): field is DynamicFieldDefinition => field !== undefined);

    for (const field of fields) {
      this.staticOverrideForm.addControl(
        field.key,
        createTaskPropertyControl(field, node.designTimeInputOverrides[field.key]),
        { emitEvent: false },
      );
    }

    this.staticOverrideDraftKeys.set(draftKeys);
    this.staticOverrideFields.set(fields);
    this.syncingStaticOverrideForm = false;
  }

  private persistStaticOverridesFromForm(): void {
    if (this.syncingStaticOverrideForm) {
      return;
    }

    const node = this.nodeSettingsNode();
    if (!node) {
      return;
    }

    const overrides: Record<string, unknown> = {};
    for (const field of this.staticOverrideFields()) {
      const control = this.staticOverrideForm.get(field.key);
      if (!control || control.invalid) {
        continue;
      }

      const value = this.serializeStaticOverrideValue(field, control.value);
      if (value !== undefined) {
        overrides[field.key] = value;
      }
    }

    if (this.recordsShallowEqual(node.designTimeInputOverrides, overrides)) {
      return;
    }

    this.editorStore.updateNodeDesignTimeOverrides(node.id, overrides);
  }

  private serializeStaticOverrideValue(field: DynamicFieldDefinition, value: unknown): unknown {
    const serializedValue = serializeTaskPropertyControlValue(field, value);
    if (serializedValue === null || serializedValue === undefined) {
      return undefined;
    }

    if (typeof serializedValue === 'string') {
      const normalized = serializedValue.trim();
      return normalized.length > 0 ? normalized : undefined;
    }

    return serializedValue;
  }

  private recordsShallowEqual(left: Record<string, unknown>, right: Record<string, unknown>): boolean {
    const leftKeys = Object.keys(left).sort();
    const rightKeys = Object.keys(right).sort();
    if (leftKeys.length !== rightKeys.length) {
      return false;
    }

    return leftKeys.every((key, index) => key === rightKeys[index] && left[key] === right[key]);
  }

  private filterBlueprints(
    blueprints: BlueprintViewModel[],
    query: unknown,
  ): BlueprintViewModel[] {
    const trimmedQuery = typeof query === 'string' ? query.trim() : '';
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
        const description = getLatestTaskVersion(task.versions)?.description?.trim() || 'Available task';
        const displayName = this.taskDefinitionDisplayName(task);

        return {
          task,
          displayName,
          icon: this.getTaskIcon(category.label, task),
          categoryLabel: category.label,
          description,
          tone: category.tone,
          searchText: `${displayName} ${task.name} ${category.label} ${description}`.toLocaleLowerCase(),
        };
      })
      .sort((left, right) => left.displayName.localeCompare(right.displayName));
  }

  protected taskDefinitionDisplayName(task: ITaskDefinition | null | undefined): string {
    const displayName = task?.displayName?.trim();
    return displayName && displayName.length > 0
      ? displayName
      : task?.name ?? '';
  }

  private getTaskCategory(task: ITaskDefinition): {
    label: 'Trigger' | 'Logic' | 'Action';
    tone: 'trigger' | 'logic' | 'action';
  } {
    const version = getLatestTaskVersion(task.versions);
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
