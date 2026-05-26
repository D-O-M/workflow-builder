import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { ReactiveFormsModule, FormControl } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import {
  BlueprintEditorService,
  BlueprintViewModel,
  BlueprintVersionViewModel,
  ProcessSearchBlueprintFilter,
  ProcessSearchNodeFilter,
  ProcessSearchPropertyFilter,
  ProcessSearchPropertyScope,
  ProcessSearchRequest,
  ProcessSearchStatus,
} from '../domain/blueprint-editor.service';
import { TaskPropertyType } from '../domain/node-model';
import { normalizeTaskPropertyDefinitions } from '../domain/task-property-form-utils';
import { ITaskDefinition, ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

type QuickRangeKey = '15m' | '1h' | '6h' | '24h' | '7d' | 'custom';
type SearchPanelIndex = 0 | 1 | 2;

interface QuickRangeOption {
  key: QuickRangeKey;
  label: string;
  minutes: number | null;
}

interface SearchPanelDefinition {
  index: SearchPanelIndex;
  label: string;
  icon: string;
}

interface SearchSummaryChip {
  label: string;
  tone?: 'danger';
}

interface ProcessSearchDialogData {
  blueprints: BlueprintViewModel[];
  taskDefinitions: ITaskDefinition[];
  initialRequest: ProcessSearchRequest | null;
}

interface NodeFilterRowState {
  key: string;
  blueprintDefinitionId: number;
  blueprintNodeId: string;
  statuses: ProcessSearchStatus[];
}

interface PropertyFilterRowState {
  id: string;
  optionKey: string | null;
  propertySearchQuery: string;
  value: string;
}

interface NodeCatalogItem {
  key: string;
  blueprintDefinitionId: number;
  blueprintNodeId: string;
  blueprintName: string;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  taskName: string;
  searchText: string;
  label: string;
  subtitle: string;
}

interface PropertyCatalogItem {
  key: string;
  scope: ProcessSearchPropertyScope;
  blueprintDefinitionId?: number | null;
  blueprintNodeId?: string | null;
  propertyKey: string;
  propertyType: TaskPropertyType;
  enumValues: string[];
  label: string;
  subtitle: string;
  searchText: string;
}

interface PropertyOptionGroup {
  key: ProcessSearchPropertyScope;
  label: string;
  options: PropertyCatalogItem[];
}

const QUICK_RANGE_OPTIONS: QuickRangeOption[] = [
  { key: '15m', label: '15m', minutes: 15 },
  { key: '1h', label: '1h', minutes: 60 },
  { key: '6h', label: '6h', minutes: 360 },
  { key: '24h', label: '24h', minutes: 1_440 },
  { key: '7d', label: '7d', minutes: 10_080 },
  { key: 'custom', label: 'Custom', minutes: null },
];

const PROCESS_STATUS_OPTIONS: ProcessSearchStatus[] = ['Pending', 'InProgress', 'Completed', 'Failed'];

const SEARCH_PANELS: SearchPanelDefinition[] = [
  { index: 0, label: 'Scope', icon: 'account_tree' },
  { index: 1, label: 'Execution', icon: 'schedule' },
  { index: 2, label: 'Properties', icon: 'data_object' },
];

@Component({
  selector: 'app-process-search-dialog',
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatChipsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
  ],
  templateUrl: './process-search-dialog.component.html',
  styleUrl: './process-search-dialog.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProcessSearchDialogComponent {
  private readonly data = inject<ProcessSearchDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ProcessSearchDialogComponent, ProcessSearchRequest | null>);
  private readonly blueprintEditor = inject(BlueprintEditorService);
  private readonly host = inject<ElementRef<HTMLElement> | null>(ElementRef, { optional: true });

  protected readonly quickRangeOptions = QUICK_RANGE_OPTIONS;
  protected readonly processStatusOptions = PROCESS_STATUS_OPTIONS;
  protected readonly searchPanels = SEARCH_PANELS;

  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
  protected readonly nodeSearch = new FormControl('', { nonNullable: true });
  private readonly blueprintSearchQuery = toSignal(this.blueprintSearch.valueChanges, {
    initialValue: this.blueprintSearch.value,
  });
  private readonly nodeSearchQuery = toSignal(this.nodeSearch.valueChanges, {
    initialValue: this.nodeSearch.value,
  });

  protected readonly selectedBlueprintIds = signal<number[]>([]);
  protected readonly selectedBlueprintStatusFilters = signal<Record<number, ProcessSearchStatus[]>>({});
  protected readonly selectedNodeFilters = signal<NodeFilterRowState[]>([]);
  protected readonly propertyFilterRows = signal<PropertyFilterRowState[]>([]);
  protected readonly selectedProcessStatuses = signal<ProcessSearchStatus[]>([]);
  protected readonly quickRange = signal<QuickRangeKey>('15m');
  protected readonly createdFromInput = signal(this.defaultCreatedFromValue());
  protected readonly createdToInput = signal(this.defaultCreatedToValue());
  protected readonly selectedTabIndex = signal(0);

  protected readonly blueprints = computed(() =>
    [...this.data.blueprints].sort((left, right) => left.name.localeCompare(right.name)),
  );
  private readonly taskDefinitionsById = computed(() =>
    new Map(this.data.taskDefinitions.map((taskDefinition) => [taskDefinition.id, taskDefinition] as const)),
  );
  protected readonly selectedBlueprints = computed(() =>
    this.blueprints().filter((blueprint) => this.selectedBlueprintIds().includes(blueprint.id)),
  );
  protected readonly selectedBlueprintRows = computed(() => {
    const statusesByBlueprintId = this.selectedBlueprintStatusFilters();
    return this.selectedBlueprints().map((blueprint) => ({
      blueprint,
      statuses: statusesByBlueprintId[blueprint.id] ?? [],
    }));
  });
  protected readonly nodeCatalog = computed(() =>
    this.buildNodeCatalog(this.blueprints(), this.taskDefinitionsById()),
  );
  protected readonly scopedNodeCatalog = computed(() => {
    const selectedBlueprintIds = this.selectedBlueprintIds();
    if (selectedBlueprintIds.length === 0) {
      return this.nodeCatalog();
    }

    const selectedIds = new Set(selectedBlueprintIds);
    return this.nodeCatalog().filter((node) => selectedIds.has(node.blueprintDefinitionId));
  });
  private readonly scopedNodeCatalogByKey = computed(() =>
    new Map(this.scopedNodeCatalog().map((node) => [node.key, node] as const)),
  );
  protected readonly selectedNodeCatalog = computed(() => {
    return this.selectedNodeFilters()
      .map((row) => this.scopedNodeCatalogByKey().get(row.key))
      .filter((row): row is NodeCatalogItem => row !== undefined);
  });
  protected readonly selectedNodeRows = computed(() =>
    this.selectedNodeFilters()
      .map((row) => ({
        ...row,
        node: this.scopedNodeCatalogByKey().get(row.key) ?? null,
      }))
      .filter((row): row is NodeFilterRowState & { node: NodeCatalogItem } => row.node !== null),
  );
  protected readonly filteredBlueprintOptions = computed(() =>
    this.filterBlueprintOptions(this.blueprints(), this.selectedBlueprintIds(), this.blueprintSearchQuery()),
  );
  protected readonly filteredNodeOptions = computed(() =>
    this.filterNodeOptions(this.scopedNodeCatalog(), this.selectedNodeFilters(), this.nodeSearchQuery()),
  );
  protected readonly propertyCatalog = computed(() =>
    this.buildPropertyCatalog(
      this.blueprints(),
      this.selectedBlueprintIds(),
      this.scopedNodeCatalog(),
      this.selectedNodeCatalog(),
      this.taskDefinitionsById(),
    ),
  );
  protected readonly selectedPropertyRows = computed(() => {
    const propertyByKey = new Map(this.propertyCatalog().map((option) => [option.key, option] as const));
    return this.propertyFilterRows().map((row) => ({
      ...row,
      option: row.optionKey ? propertyByKey.get(row.optionKey) ?? null : null,
    }));
  });
  protected readonly searchContextSummary = computed(() => {
    const quickRangeLabel = this.quickRangeOptions.find((option) => option.key === this.quickRange())?.label ?? 'Custom';
    const windowLabel = this.quickRange() === 'custom' ? 'custom window' : `${quickRangeLabel} window`;
    const blueprintCount = this.selectedBlueprintIds().length;
    const blueprintLabel = blueprintCount === 0
      ? 'all blueprints'
      : blueprintCount === 1
        ? '1 blueprint'
        : `${blueprintCount} blueprints`;
    const nodeCount = this.selectedNodeFilters().length;
    const nodeLabel = nodeCount === 0
      ? null
      : nodeCount === 1
        ? '1 node'
        : `${nodeCount} nodes`;
    const blueprintStatusSelections = this.selectedBlueprintRows().reduce((total, row) => total + row.statuses.length, 0);
    const nodeStatusSelections = this.selectedNodeFilters().reduce((total, row) => total + row.statuses.length, 0);
    const processStatusSelections = this.selectedBlueprintIds().length === 0 ? this.selectedProcessStatuses().length : 0;
    const statusCount = blueprintStatusSelections + nodeStatusSelections + processStatusSelections;
    const statusLabel = statusCount === 0
      ? 'any status'
      : statusCount === 1
        ? '1 status selection'
        : `${statusCount} status selections`;
    const propertyCount = this.propertyFilterRows().length;
    const propertyLabel = propertyCount === 0
      ? null
      : propertyCount === 1
        ? '1 property filter'
        : `${propertyCount} property filters`;

    return [windowLabel, blueprintLabel, nodeLabel, statusLabel, propertyLabel].filter(Boolean).join(' · ');
  });
  protected readonly propertyScopeDescription = computed(() => {
    const blueprintCount = this.selectedBlueprintIds().length;
    const nodeCount = this.selectedNodeFilters().length;
    if (nodeCount > 0) {
      return `Property choices are limited to ${nodeCount === 1 ? 'the selected node' : `${nodeCount} selected nodes`}.`;
    }

    if (blueprintCount > 0) {
      return `Property choices are computed from ${blueprintCount === 1 ? 'the selected blueprint' : `${blueprintCount} selected blueprints`}.`;
    }

    return 'Property choices span all design-time blueprint metadata until a scope is selected.';
  });
  protected readonly hasDateWindowError = computed(() => {
    const createdFrom = this.parseDateTimeLocal(this.createdFromInput());
    const createdTo = this.parseDateTimeLocal(this.createdToInput());
    return !createdFrom || !createdTo || createdFrom > createdTo;
  });
  protected readonly hasInvalidPropertyRows = computed(() =>
    this.selectedPropertyRows().some((row) => row.option === null || row.value.trim().length === 0),
  );
  protected readonly incompletePropertyRowCount = computed(() =>
    this.selectedPropertyRows().filter((row) => row.option === null || row.value.trim().length === 0).length,
  );
  protected readonly canApply = computed(() =>
    !this.hasDateWindowError() && !this.hasInvalidPropertyRows(),
  );
  protected readonly applyBlockReason = computed(() => {
    const incompleteRows = this.incompletePropertyRowCount();
    const hasDateWindowError = this.hasDateWindowError();
    if (hasDateWindowError && incompleteRows > 0) {
      return incompleteRows === 1
        ? 'Fix execution window and 1 incomplete property filter.'
        : `Fix execution window and ${incompleteRows} incomplete property filters.`;
    }

    if (hasDateWindowError) {
      return 'Choose a valid date range before applying.';
    }

    if (incompleteRows > 0) {
      return incompleteRows === 1
        ? 'Complete 1 property filter before applying.'
        : `Complete ${incompleteRows} property filters before applying.`;
    }

    return null;
  });
  protected readonly searchSummaryChips = computed<SearchSummaryChip[]>(() => {
    const chips: SearchSummaryChip[] = [
      { label: this.windowSummaryLabel() },
      { label: this.blueprintScopeSummary() },
      { label: this.statusSummaryLabel() },
    ];

    if (this.selectedNodeFilters().length > 0) {
      chips.splice(2, 0, { label: this.nodeScopeSummary() });
    }

    if (this.propertyFilterRows().length > 0) {
      chips.push({ label: this.propertySummaryLabel() });
    }

    if (this.hasDateWindowError() || this.incompletePropertyRowCount() > 0) {
      chips.push({ label: 'Needs attention', tone: 'danger' });
    }

    return chips;
  });
  constructor() {
    this.restoreInitialRequest(this.data.initialRequest);

    effect(() => {
      const validNodeKeys = new Set(this.scopedNodeCatalog().map((node) => node.key));
      const currentRows = this.selectedNodeFilters();
      const nextRows = currentRows.filter((row) => validNodeKeys.has(row.key));
      if (nextRows.length !== currentRows.length) {
        untracked(() => this.selectedNodeFilters.set(nextRows));
      }
    });

    effect(() => {
      const validPropertyKeys = new Set(this.propertyCatalog().map((option) => option.key));
      const currentRows = this.propertyFilterRows();
      const nextRows = currentRows.filter((row) => row.optionKey === null || validPropertyKeys.has(row.optionKey));
      if (nextRows.length !== currentRows.length) {
        untracked(() => this.propertyFilterRows.set(nextRows));
      }
    });
  }

  protected onBlueprintOptionSelected(event: MatAutocompleteSelectedEvent): void {
    const blueprintId = Number(event.option.value);
    if (Number.isNaN(blueprintId) || this.selectedBlueprintIds().includes(blueprintId)) {
      return;
    }

    this.selectedBlueprintIds.update((current) => [...current, blueprintId].sort((left, right) => left - right));
    this.selectedBlueprintStatusFilters.update((current) => ({
      ...current,
      [blueprintId]: current[blueprintId] ?? [],
    }));
    this.sanitizeScopedSelections();
    this.blueprintSearch.setValue('');
    this.scrollRowIntoView(`blueprint-${blueprintId}`);
  }

  protected onNodeOptionSelected(event: MatAutocompleteSelectedEvent): void {
    const selectedKey = String(event.option.value);
    const node = this.scopedNodeCatalog().find((candidate) => candidate.key === selectedKey);
    if (!node || this.selectedNodeFilters().some((filter) => filter.key === selectedKey)) {
      return;
    }

    this.selectedNodeFilters.update((current) => [
      ...current,
      {
        key: node.key,
        blueprintDefinitionId: node.blueprintDefinitionId,
        blueprintNodeId: node.blueprintNodeId,
        statuses: [],
      },
    ]);
    this.sanitizeScopedSelections();
    this.nodeSearch.setValue('');
    this.scrollRowIntoView(`node-${node.key}`);
  }

  protected removeBlueprint(blueprintId: number): void {
    this.selectedBlueprintIds.update((current) => current.filter((candidate) => candidate !== blueprintId));
    this.selectedBlueprintStatusFilters.update((current) => {
      const next = { ...current };
      delete next[blueprintId];
      return next;
    });
    this.sanitizeScopedSelections();
  }

  protected updateBlueprintStatuses(blueprintId: number, statuses: ProcessSearchStatus[]): void {
    this.selectedBlueprintStatusFilters.update((current) => ({
      ...current,
      [blueprintId]: [...statuses],
    }));
  }

  protected toggleBlueprintStatus(blueprintId: number, status: ProcessSearchStatus): void {
    const currentStatuses = this.selectedBlueprintStatusFilters()[blueprintId] ?? [];
    const nextStatuses = currentStatuses.includes(status)
      ? currentStatuses.filter((candidate) => candidate !== status)
      : [...currentStatuses, status];
    this.updateBlueprintStatuses(blueprintId, nextStatuses);
  }

  protected removeNodeFilter(key: string): void {
    this.selectedNodeFilters.update((current) => current.filter((row) => row.key !== key));
    this.sanitizeScopedSelections();
  }

  protected updateNodeStatuses(key: string, statuses: ProcessSearchStatus[]): void {
    this.selectedNodeFilters.update((current) => current.map((row) =>
      row.key === key
        ? { ...row, statuses: [...statuses] }
        : row));
  }

  protected toggleNodeStatus(key: string, status: ProcessSearchStatus): void {
    const currentStatuses = this.selectedNodeFilters().find((row) => row.key === key)?.statuses ?? [];
    const nextStatuses = currentStatuses.includes(status)
      ? currentStatuses.filter((candidate) => candidate !== status)
      : [...currentStatuses, status];
    this.updateNodeStatuses(key, nextStatuses);
  }

  protected addPropertyFilterRow(): void {
    if (this.hasInvalidPropertyRows()) {
      return;
    }

    if (this.propertyCatalog().length === 0) {
      return;
    }

    const rowId = crypto.randomUUID();
    this.propertyFilterRows.update((current) => [
      ...current,
      {
        id: rowId,
        optionKey: null,
        propertySearchQuery: '',
        value: '',
      },
    ]);
    this.scrollRowIntoView(`property-${rowId}`);
  }

  protected removePropertyFilterRow(rowId: string): void {
    this.propertyFilterRows.update((current) => current.filter((row) => row.id !== rowId));
  }

  protected updatePropertyOption(rowId: string, optionKey: string | null): void {
    const option = optionKey ? this.propertyCatalog().find((candidate) => candidate.key === optionKey) : null;
    this.propertyFilterRows.update((current) => current.map((row) =>
      row.id === rowId
        ? { ...row, optionKey, propertySearchQuery: option ? this.propertyOptionDisplayText(option) : '' }
        : row));
  }

  protected updatePropertySearchQuery(rowId: string, query: string): void {
    this.propertyFilterRows.update((current) => current.map((row) => {
      if (row.id !== rowId) {
        return row;
      }

      const selectedOption = row.optionKey
        ? this.propertyCatalog().find((option) => option.key === row.optionKey)
        : null;
      const selectedText = selectedOption ? this.propertyOptionDisplayText(selectedOption) : '';
      return {
        ...row,
        propertySearchQuery: query,
        optionKey: query === selectedText ? row.optionKey : null,
      };
    }));
  }

  protected onPropertyOptionSelected(rowId: string, event: MatAutocompleteSelectedEvent): void {
    this.updatePropertyOption(rowId, String(event.option.value));
  }

  protected updatePropertyValue(rowId: string, value: string): void {
    this.propertyFilterRows.update((current) => current.map((row) =>
      row.id === rowId
        ? { ...row, value }
        : row));
  }

  protected toggleProcessStatus(status: ProcessSearchStatus): void {
    this.selectedProcessStatuses.update((current) =>
      current.includes(status)
        ? current.filter((candidate) => candidate !== status)
        : [...current, status]);
  }

  protected selectQuickRange(option: QuickRangeOption): void {
    this.quickRange.set(option.key);
    if (option.minutes === null) {
      return;
    }

    const now = new Date();
    const start = new Date(now.getTime() - option.minutes * 60_000);
    this.createdFromInput.set(this.formatDateTimeLocal(start));
    this.createdToInput.set(this.formatDateTimeLocal(now));
  }

  protected onCustomDateChanged(): void {
    this.quickRange.set('custom');
  }

  protected cancel(): void {
    this.dialogRef.close(null);
  }

  protected resetAll(): void {
    this.selectedBlueprintIds.set([]);
    this.selectedNodeFilters.set([]);
    this.propertyFilterRows.set([]);
    this.selectedProcessStatuses.set([]);
    this.selectedBlueprintStatusFilters.set({});
    this.quickRange.set('15m');
    this.createdFromInput.set(this.defaultCreatedFromValue());
    this.createdToInput.set(this.defaultCreatedToValue());
    this.blueprintSearch.setValue('');
    this.nodeSearch.setValue('');
    this.scrollDialogContentToTop();
  }

  protected apply(): void {
    if (!this.canApply()) {
      return;
    }

    const createdFrom = this.parseDateTimeLocal(this.createdFromInput());
    const createdTo = this.parseDateTimeLocal(this.createdToInput());
    if (!createdFrom || !createdTo) {
      return;
    }

    const propertyByKey = new Map(this.propertyCatalog().map((option) => [option.key, option] as const));
    const blueprintFilters: ProcessSearchBlueprintFilter[] = this.selectedBlueprintIds().map((blueprintDefinitionId) => ({
      blueprintDefinitionId,
      statuses: [...(this.selectedBlueprintStatusFilters()[blueprintDefinitionId] ?? [])],
    }));
    const propertyFilters: ProcessSearchPropertyFilter[] = this.propertyFilterRows()
      .flatMap((row) => {
        const option = row.optionKey ? propertyByKey.get(row.optionKey) : null;
        const value = row.value.trim();
        if (!option || value.length === 0) {
          return [];
        }

        return [{
          scope: option.scope,
          blueprintDefinitionId: option.blueprintDefinitionId ?? null,
          blueprintNodeId: option.blueprintNodeId ?? null,
          propertyKey: option.propertyKey,
          propertyType: option.propertyType,
          value,
        } satisfies ProcessSearchPropertyFilter];
      });

    const request: ProcessSearchRequest = {
      blueprintDefinitionIds: [...this.selectedBlueprintIds()],
      blueprintFilters,
      processStatuses: this.selectedBlueprintIds().length === 0
        ? [...this.selectedProcessStatuses()]
        : [],
      createdFrom: createdFrom.toISOString(),
      createdTo: createdTo.toISOString(),
      nodeFilters: this.selectedNodeFilters().map((row) => ({
        blueprintDefinitionId: row.blueprintDefinitionId,
        blueprintNodeId: row.blueprintNodeId,
        statuses: [...row.statuses],
      })),
      propertyFilters,
      limit: 25,
    };

    this.dialogRef.close(request);
  }

  protected statusesLabel(statuses: ProcessSearchStatus[]): string {
    if (statuses.length === 0) {
      return 'Any status';
    }

    if (statuses.length === PROCESS_STATUS_OPTIONS.length) {
      return 'All statuses';
    }

    return statuses.map((status) => this.statusLabel(status)).join(', ');
  }

  protected statusLabel(status: ProcessSearchStatus): string {
    switch (status) {
      case 'InProgress':
        return 'Running';
      case 'Completed':
        return 'Completed';
      case 'Failed':
        return 'Failed';
      default:
        return 'Pending';
    }
  }

  protected statusIcon(status: ProcessSearchStatus): string {
    switch (status) {
      case 'InProgress':
        return 'sync';
      case 'Completed':
        return 'check_circle';
      case 'Failed':
        return 'error';
      default:
        return 'schedule';
    }
  }

  protected valuePlaceholder(propertyType: TaskPropertyType): string {
    switch (propertyType) {
      case 'Boolean':
        return 'Select true or false';
      case 'Guid':
        return 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx';
      case 'Integer':
      case 'Decimal':
        return 'Enter a number';
      case 'Date':
        return 'Select a date';
      case 'DateTime':
        return 'Select a date and time';
      case 'Enum':
        return 'Select an option';
      default:
        return 'Enter an exact value';
    }
  }

  protected propertyInputType(propertyType: TaskPropertyType): string {
    switch (propertyType) {
      case 'Integer':
      case 'Decimal':
        return 'number';
      case 'Date':
        return 'date';
      case 'DateTime':
        return 'datetime-local';
      default:
        return 'text';
    }
  }

  protected propertyUsesSelect(propertyType: TaskPropertyType): boolean {
    return propertyType === 'Boolean' || propertyType === 'Enum';
  }

  protected propertySelectOptions(option: PropertyCatalogItem | null | undefined): string[] {
    if (!option) {
      return [];
    }

    return option.propertyType === 'Boolean' ? ['true', 'false'] : option.enumValues;
  }

  protected propertyInputDisplay(row: PropertyFilterRowState & { option: PropertyCatalogItem | null }): string {
    return row.propertySearchQuery ?? (row.option ? this.propertyOptionDisplayText(row.option) : '');
  }

  protected propertyRowHint(option: PropertyCatalogItem | null): string {
    if (!option) {
      return 'Search by node, channel, or property name';
    }

    return `${this.propertyScopeShortLabel(option.scope)} · ${option.subtitle} · ${option.propertyType}`;
  }

  protected propertyOptionPrimary(option: PropertyCatalogItem): string {
    return option.propertyKey;
  }

  protected propertyOptionMeta(option: PropertyCatalogItem): string {
    switch (option.scope) {
      case 'NodeInput':
        return `${this.propertyTaskName(option)} · Input · ${option.subtitle}`;
      case 'NodeOutput':
        return `${this.propertyTaskName(option)} · Output · ${option.subtitle}`;
    }
  }

  protected filteredPropertyOptionGroups(rowId: string): PropertyOptionGroup[] {
    const row = this.propertyFilterRows().find((candidate) => candidate.id === rowId);
    if (!row) {
      return [];
    }

    const selectedKeys = new Set(
      this.propertyFilterRows()
        .filter((candidate) => candidate.id !== rowId)
        .map((candidate) => candidate.optionKey)
        .filter((key): key is string => key !== null),
    );
    const normalizedQuery = (row.propertySearchQuery ?? '').trim().toLocaleLowerCase();
    const groups = new Map<ProcessSearchPropertyScope, PropertyCatalogItem[]>();
    for (const option of this.propertyCatalog()) {
      if (selectedKeys.has(option.key)) {
        continue;
      }

      if (
        normalizedQuery.length > 0
        && !option.searchText.includes(normalizedQuery)
        && !option.label.toLocaleLowerCase().includes(normalizedQuery)
        && !this.propertyOptionMeta(option).toLocaleLowerCase().includes(normalizedQuery)
      ) {
        continue;
      }

      const options = groups.get(option.scope) ?? [];
      options.push(option);
      groups.set(option.scope, options);
    }

    return (['NodeInput', 'NodeOutput'] satisfies ProcessSearchPropertyScope[])
      .map((scope) => ({
        key: scope,
        label: this.propertyScopeGroupLabel(scope),
        options: groups.get(scope) ?? [],
      }))
      .filter((group) => group.options.length > 0);
  }

  protected onTabIndexChange(index: number): void {
    if (!this.isSearchPanelIndex(index)) {
      return;
    }

    this.selectSearchTab(index);
  }

  protected selectSearchTab(index: SearchPanelIndex): void {
    this.selectedTabIndex.set(index);
    this.scrollDialogContentToTop();
  }

  protected searchPanelSummary(index: SearchPanelIndex): string {
    switch (index) {
      case 0:
        return this.scopePanelSummary();
      case 1:
        return this.executionPanelSummary();
      case 2:
        return this.propertySummaryLabel();
    }
  }

  protected searchPanelBadge(index: SearchPanelIndex): string | null {
    switch (index) {
      case 0: {
        const selectedCount = this.selectedBlueprintIds().length + this.selectedNodeFilters().length;
        return selectedCount === 0 ? null : String(selectedCount);
      }
      case 1:
        if (this.hasDateWindowError()) {
          return '!';
        }

        return this.statusSelectionCount() === 0 ? null : String(this.statusSelectionCount());
      case 2:
        if (this.incompletePropertyRowCount() > 0) {
          return '!';
        }

        return this.propertyFilterRows().length === 0 ? null : String(this.propertyFilterRows().length);
    }
  }

  protected searchPanelIssue(index: SearchPanelIndex): boolean {
    switch (index) {
      case 1:
        return this.hasDateWindowError();
      case 2:
        return this.incompletePropertyRowCount() > 0;
      default:
        return false;
    }
  }

  protected scopePanelSummary(): string {
    return `${this.blueprintScopeSummary()} · ${this.nodeScopeSummary()}`;
  }

  protected executionPanelSummary(): string {
    return `${this.windowSummaryLabel()} · ${this.statusSummaryLabel()}`;
  }

  private isSearchPanelIndex(index: number): index is SearchPanelIndex {
    return index === 0 || index === 1 || index === 2;
  }

  private windowSummaryLabel(): string {
    const quickRangeLabel = this.quickRangeOptions.find((option) => option.key === this.quickRange())?.label ?? 'Custom';
    return this.quickRange() === 'custom' ? 'Custom window' : `${quickRangeLabel} window`;
  }

  private blueprintScopeSummary(): string {
    const count = this.selectedBlueprintIds().length;
    if (count === 0) {
      return 'All blueprints';
    }

    return count === 1 ? '1 blueprint' : `${count} blueprints`;
  }

  private nodeScopeSummary(): string {
    const count = this.selectedNodeFilters().length;
    if (count === 0) {
      return 'Any step';
    }

    return count === 1 ? '1 step' : `${count} steps`;
  }

  private propertySummaryLabel(): string {
    const count = this.propertyFilterRows().length;
    if (count === 0) {
      return 'No property filters';
    }

    return count === 1 ? '1 property filter' : `${count} property filters`;
  }

  private statusSummaryLabel(): string {
    const count = this.statusSelectionCount();
    if (count === 0) {
      return 'Any status';
    }

    return count === 1 ? '1 status' : `${count} statuses`;
  }

  private statusSelectionCount(): number {
    const blueprintStatusSelections = this.selectedBlueprintRows().reduce((total, row) => total + row.statuses.length, 0);
    const nodeStatusSelections = this.selectedNodeFilters().reduce((total, row) => total + row.statuses.length, 0);
    const processStatusSelections = this.selectedBlueprintIds().length === 0 ? this.selectedProcessStatuses().length : 0;
    return blueprintStatusSelections + nodeStatusSelections + processStatusSelections;
  }

  private restoreInitialRequest(initialRequest: ProcessSearchRequest | null): void {
    if (!initialRequest) {
      this.resetAll();
      this.selectedTabIndex.set(0);
      return;
    }

    const initialBlueprintIds = [
      ...initialRequest.blueprintDefinitionIds,
      ...(initialRequest.blueprintFilters ?? []).map((filter) => filter.blueprintDefinitionId),
    ];
    this.selectedBlueprintIds.set([...new Set(initialBlueprintIds)].sort((left, right) => left - right));
    this.selectedBlueprintStatusFilters.set(
      Object.fromEntries((initialRequest.blueprintFilters ?? []).map((filter) => [
        filter.blueprintDefinitionId,
        [...filter.statuses],
      ])),
    );
    this.selectedNodeFilters.set(initialRequest.nodeFilters.map((filter) => ({
      key: `${filter.blueprintDefinitionId}:${filter.blueprintNodeId}`,
      blueprintDefinitionId: filter.blueprintDefinitionId,
      blueprintNodeId: filter.blueprintNodeId,
      statuses: [...filter.statuses],
    })));
    this.selectedProcessStatuses.set([...initialRequest.processStatuses]);
    this.createdFromInput.set(this.formatDateTimeLocal(new Date(initialRequest.createdFrom)));
    this.createdToInput.set(this.formatDateTimeLocal(new Date(initialRequest.createdTo)));
    this.quickRange.set('custom');

    const propertyCatalog = this.buildPropertyCatalog(
      this.data.blueprints,
      [...initialRequest.blueprintDefinitionIds].sort((left, right) => left - right),
      this.buildNodeCatalog(this.data.blueprints, new Map(this.data.taskDefinitions.map((taskDefinition) => [taskDefinition.id, taskDefinition] as const))),
      [],
      new Map(this.data.taskDefinitions.map((taskDefinition) => [taskDefinition.id, taskDefinition] as const)),
    );
    const propertyLookup = new Map(propertyCatalog.map((option) => [this.propertyKey(option), option.key] as const));
    const propertyByKey = new Map(propertyCatalog.map((option) => [option.key, option] as const));

    this.propertyFilterRows.set(initialRequest.propertyFilters.map((filter) => ({
      id: crypto.randomUUID(),
      optionKey: propertyLookup.get(this.propertyKey(filter)) ?? null,
      propertySearchQuery: (() => {
        const optionKey = propertyLookup.get(this.propertyKey(filter)) ?? null;
        const option = optionKey ? propertyByKey.get(optionKey) : null;
        return option ? this.propertyOptionDisplayText(option) : filter.propertyKey;
      })(),
      value: filter.value,
    })));
    this.selectedTabIndex.set(this.initialTabIndexForRequest(initialRequest));
    this.scrollDialogContentToTop();
  }

  private defaultCreatedFromValue(): string {
    const now = new Date();
    const start = new Date(now.getTime() - 15 * 60_000);
    return this.formatDateTimeLocal(start);
  }

  private defaultCreatedToValue(): string {
    return this.formatDateTimeLocal(new Date());
  }

  private parseDateTimeLocal(value: string): Date | null {
    if (!value) {
      return null;
    }

    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }

  private formatDateTimeLocal(value: Date): string {
    const localValue = new Date(value.getTime() - value.getTimezoneOffset() * 60_000);
    return localValue.toISOString().slice(0, 16);
  }

  private filterBlueprintOptions(
    blueprints: BlueprintViewModel[],
    selectedBlueprintIds: number[],
    query: string,
  ): BlueprintViewModel[] {
    const selectedIds = new Set(selectedBlueprintIds);
    const normalizedQuery = query.trim().toLocaleLowerCase();
    return blueprints.filter((blueprint) => {
      if (selectedIds.has(blueprint.id)) {
        return false;
      }

      return normalizedQuery.length === 0
        || blueprint.name.toLocaleLowerCase().includes(normalizedQuery);
    });
  }

  private filterNodeOptions(
    nodes: NodeCatalogItem[],
    selectedNodeFilters: NodeFilterRowState[],
    query: string,
  ): NodeCatalogItem[] {
    const selectedKeys = new Set(selectedNodeFilters.map((filter) => filter.key));
    const normalizedQuery = query.trim().toLocaleLowerCase();
    return nodes.filter((node) => {
      if (selectedKeys.has(node.key)) {
        return false;
      }

      return normalizedQuery.length === 0 || node.searchText.includes(normalizedQuery);
    });
  }

  private buildNodeCatalog(
    blueprints: BlueprintViewModel[],
    taskDefinitionsById: Map<number, ITaskDefinition>,
  ): NodeCatalogItem[] {
    return blueprints
      .flatMap((blueprint) => blueprint.versions.flatMap((version) =>
        version.tasks.map((task) => {
          const taskDefinition = taskDefinitionsById.get(task.taskDefinitionId ?? 0);
          return {
            key: `${blueprint.id}:${task.instanceId}`,
            blueprintDefinitionId: blueprint.id,
            blueprintNodeId: task.instanceId,
            blueprintName: blueprint.name,
            blueprintVersionId: version.blueprintVersionId,
            blueprintVersionNumber: version.version,
            taskName: taskDefinition?.name ?? `Task ${task.taskDefinitionId}`,
            label: taskDefinition?.name ?? `Task ${task.taskDefinitionId}`,
            subtitle: `${blueprint.name} · Version ${version.version}`,
            searchText: `${blueprint.name} ${taskDefinition?.name ?? task.taskDefinitionId} version ${version.version}`.toLocaleLowerCase(),
          } satisfies NodeCatalogItem;
        })))
      .sort((left, right) =>
        left.blueprintName.localeCompare(right.blueprintName)
        || left.blueprintVersionNumber - right.blueprintVersionNumber
        || left.taskName.localeCompare(right.taskName));
  }

  private buildPropertyCatalog(
    blueprints: BlueprintViewModel[],
    selectedBlueprintIds: number[],
    scopedNodeCatalog: NodeCatalogItem[],
    selectedNodeCatalog: NodeCatalogItem[],
    taskDefinitionsById: Map<number, ITaskDefinition>,
  ): PropertyCatalogItem[] {
    const blueprintScope = selectedBlueprintIds.length === 0
      ? blueprints
      : blueprints.filter((blueprint) => selectedBlueprintIds.includes(blueprint.id));
    const nodeScopeKeys = selectedNodeCatalog.length === 0
      ? new Set(scopedNodeCatalog.map((node) => node.key))
      : new Set(selectedNodeCatalog.map((node) => node.key));

    const propertyMap = new Map<string, PropertyCatalogItem>();
    for (const blueprint of blueprintScope) {
      for (const version of blueprint.versions) {
        for (const task of version.tasks) {
          const taskDefinition = taskDefinitionsById.get(task.taskDefinitionId ?? 0);
          const taskVersion = taskDefinition
            ? this.blueprintEditor.resolveVersionForLane(taskDefinition, task.allowedMajor, task.allowedMinor, task.resolvedVersionId)
            : null;
          const nodeKey = `${blueprint.id}:${task.instanceId}`;
          const taskName = task.nodeKind === 'CollectionIterator'
            ? 'Collection Iterator'
            : taskDefinition?.name ?? `Task ${task.taskDefinitionId ?? 0}`;

          if (!nodeScopeKeys.has(nodeKey)) {
            continue;
          }

          for (const property of normalizeTaskPropertyDefinitions(taskVersion?.inputChannel?.properties)) {
            if (property.kind.endsWith('[]')) {
              continue;
            }

            const option: PropertyCatalogItem = {
              key: `input:${blueprint.id}:${task.instanceId}:${property.name}:${property.kind}`,
              scope: 'NodeInput',
              blueprintDefinitionId: blueprint.id,
              blueprintNodeId: task.instanceId,
              propertyKey: property.name,
              propertyType: property.kind,
              enumValues: [...property.enumValues],
              label: `${taskName} Input / ${property.name}`,
              subtitle: `${blueprint.name} · Version ${version.version}`,
              searchText: `${blueprint.name} ${taskName} input ${property.name}`.toLocaleLowerCase(),
            };
            propertyMap.set(option.key, option);
          }

          const outputProperties = new Map<string, ITaskPropertyDefinition>();
          for (const channel of taskVersion?.outputChannels ?? []) {
            for (const property of normalizeTaskPropertyDefinitions(channel.properties)) {
              if (property.kind.endsWith('[]')) {
                continue;
              }

              outputProperties.set(property.name, property);
            }
          }

          for (const [propertyKey, property] of outputProperties) {
            const option: PropertyCatalogItem = {
              key: `output:${blueprint.id}:${task.instanceId}:${propertyKey}:${property.kind}`,
              scope: 'NodeOutput',
              blueprintDefinitionId: blueprint.id,
              blueprintNodeId: task.instanceId,
              propertyKey,
              propertyType: property.kind,
              enumValues: [...property.enumValues],
              label: `${taskName} Output / ${propertyKey}`,
              subtitle: `${blueprint.name} · Version ${version.version}`,
              searchText: `${blueprint.name} ${taskName} output ${propertyKey}`.toLocaleLowerCase(),
            };
            propertyMap.set(option.key, option);
          }
        }
      }
    }

    return [...propertyMap.values()].sort((left, right) =>
      left.label.localeCompare(right.label) || left.subtitle.localeCompare(right.subtitle));
  }

  private propertyKey(
    filter: Pick<ProcessSearchPropertyFilter, 'scope' | 'blueprintDefinitionId' | 'blueprintNodeId' | 'propertyKey' | 'propertyType'>,
  ): string {
    return [
      filter.scope.toLocaleLowerCase(),
      filter.blueprintDefinitionId ?? 'all',
      filter.blueprintNodeId ?? 'global',
      filter.propertyKey,
      filter.propertyType,
    ].join(':');
  }

  private sanitizeScopedSelections(): void {
    const validNodeKeys = new Set(this.scopedNodeCatalog().map((node) => node.key));
    this.selectedNodeFilters.update((current) => current.filter((row) => validNodeKeys.has(row.key)));

    const selectedBlueprintIds = new Set(this.selectedBlueprintIds());
    this.selectedBlueprintStatusFilters.update((current) => Object.fromEntries(
      Object.entries(current)
        .filter(([blueprintId]) => selectedBlueprintIds.has(Number(blueprintId))),
    ));

    const validPropertyKeys = new Set(this.propertyCatalog().map((option) => option.key));
    this.propertyFilterRows.update((current) =>
      current.filter((row) => row.optionKey === null || validPropertyKeys.has(row.optionKey)));
  }

  private propertyOptionDisplayText(option: PropertyCatalogItem): string {
    return `${option.propertyKey} (${this.propertyScopeShortLabel(option.scope)})`;
  }

  private propertyScopeShortLabel(scope: ProcessSearchPropertyScope): string {
    switch (scope) {
      case 'NodeInput':
        return 'Input';
      case 'NodeOutput':
        return 'Output';
    }
  }

  private propertyScopeGroupLabel(scope: ProcessSearchPropertyScope): string {
    switch (scope) {
      case 'NodeInput':
        return 'Node inputs';
      case 'NodeOutput':
        return 'Node outputs';
    }
  }

  private propertyTaskName(option: PropertyCatalogItem): string {
    return option.label.replace(/\s+(Input|Output)\s+\/\s+.+$/, '');
  }

  private initialTabIndexForRequest(request: ProcessSearchRequest): number {
    if (request.propertyFilters.length > 0) {
      return 2;
    }

    if (
      request.blueprintDefinitionIds.length > 0
      || (request.blueprintFilters ?? []).some((filter) => filter.statuses.length > 0)
      || request.nodeFilters.length > 0
    ) {
      return 0;
    }

    return 1;
  }

  private scrollDialogContentToTop(): void {
    this.scheduleUiWork(() => {
      const scrollTarget = this.host?.nativeElement.querySelector('.process-search-dialog__editor')
        ?? this.host?.nativeElement.querySelector('.process-search-dialog');
      scrollTarget?.scrollTo({
        top: 0,
        behavior: 'smooth',
      });
    });
  }

  private scrollRowIntoView(rowId: string): void {
    this.scheduleUiWork(() => {
      this.host?.nativeElement
        .querySelector(`[data-search-row-id="${CSS.escape(rowId)}"]`)
        ?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    });
  }

  private scheduleUiWork(callback: () => void): void {
    if (!this.host) {
      return;
    }

    const scheduler = globalThis.requestAnimationFrame
      ? (work: () => void) => globalThis.requestAnimationFrame(work)
      : (work: () => void) => globalThis.setTimeout(work, 0);
    scheduler(callback);
  }
}
