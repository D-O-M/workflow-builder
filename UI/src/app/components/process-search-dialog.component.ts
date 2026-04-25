import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { ReactiveFormsModule, FormControl } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTabsModule } from '@angular/material/tabs';
import {
  BlueprintEditorService,
  BlueprintViewModel,
  BlueprintVersionViewModel,
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

interface QuickRangeOption {
  key: QuickRangeKey;
  label: string;
  minutes: number | null;
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

const QUICK_RANGE_OPTIONS: QuickRangeOption[] = [
  { key: '15m', label: '15m', minutes: 15 },
  { key: '1h', label: '1h', minutes: 60 },
  { key: '6h', label: '6h', minutes: 360 },
  { key: '24h', label: '24h', minutes: 1_440 },
  { key: '7d', label: '7d', minutes: 10_080 },
  { key: 'custom', label: 'Custom', minutes: null },
];

const PROCESS_STATUS_OPTIONS: ProcessSearchStatus[] = ['Pending', 'InProgress', 'Completed', 'Failed'];

@Component({
  selector: 'app-process-search-dialog',
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatCardModule,
    MatChipsModule,
    MatDialogModule,
    MatDividerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTabsModule,
  ],
  templateUrl: './process-search-dialog.component.html',
  styleUrl: './process-search-dialog.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProcessSearchDialogComponent {
  private readonly data = inject<ProcessSearchDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ProcessSearchDialogComponent, ProcessSearchRequest | null>);
  private readonly blueprintEditor = inject(BlueprintEditorService);

  protected readonly quickRangeOptions = QUICK_RANGE_OPTIONS;
  protected readonly processStatusOptions = PROCESS_STATUS_OPTIONS;

  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
  protected readonly nodeSearch = new FormControl('', { nonNullable: true });
  private readonly blueprintSearchQuery = toSignal(this.blueprintSearch.valueChanges, {
    initialValue: this.blueprintSearch.value,
  });
  private readonly nodeSearchQuery = toSignal(this.nodeSearch.valueChanges, {
    initialValue: this.nodeSearch.value,
  });

  protected readonly selectedBlueprintIds = signal<number[]>([]);
  protected readonly selectedNodeFilters = signal<NodeFilterRowState[]>([]);
  protected readonly propertyFilterRows = signal<PropertyFilterRowState[]>([]);
  protected readonly selectedProcessStatuses = signal<ProcessSearchStatus[]>([]);
  protected readonly quickRange = signal<QuickRangeKey>('15m');
  protected readonly createdFromInput = signal(this.defaultCreatedFromValue());
  protected readonly createdToInput = signal(this.defaultCreatedToValue());

  protected readonly blueprints = computed(() =>
    [...this.data.blueprints].sort((left, right) => left.name.localeCompare(right.name)),
  );
  private readonly taskDefinitionsById = computed(() =>
    new Map(this.data.taskDefinitions.map((taskDefinition) => [taskDefinition.id, taskDefinition] as const)),
  );
  protected readonly selectedBlueprints = computed(() =>
    this.blueprints().filter((blueprint) => this.selectedBlueprintIds().includes(blueprint.id)),
  );
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
  protected readonly activeFilterSummary = computed(() => {
    const segments: string[] = [];
    const selectedBlueprintCount = this.selectedBlueprintIds().length;
    if (selectedBlueprintCount > 0) {
      segments.push(selectedBlueprintCount === 1 ? '1 blueprint' : `${selectedBlueprintCount} blueprints`);
    }

    const selectedNodeCount = this.selectedNodeFilters().length;
    if (selectedNodeCount > 0) {
      segments.push(selectedNodeCount === 1 ? '1 node' : `${selectedNodeCount} nodes`);
    }

    if (this.selectedProcessStatuses().length > 0) {
      segments.push(`${this.selectedProcessStatuses().length} statuses`);
    }

    if (this.propertyFilterRows().length > 0) {
      segments.push(this.propertyFilterRows().length === 1 ? '1 property filter' : `${this.propertyFilterRows().length} property filters`);
    }

    return segments;
  });
  protected readonly hasDateWindowError = computed(() => {
    const createdFrom = this.parseDateTimeLocal(this.createdFromInput());
    const createdTo = this.parseDateTimeLocal(this.createdToInput());
    return !createdFrom || !createdTo || createdFrom > createdTo;
  });
  protected readonly hasInvalidPropertyRows = computed(() =>
    this.selectedPropertyRows().some((row) => row.option === null || row.value.trim().length === 0),
  );
  protected readonly canApply = computed(() =>
    !this.hasDateWindowError() && !this.hasInvalidPropertyRows(),
  );
  protected readonly propertyPlaceholder = computed(() => {
    const propertyRows = this.selectedPropertyRows();
    const lastRow = propertyRows.at(-1);
    if (!lastRow?.option) {
      return 'Enter a value';
    }

    return this.valuePlaceholder(lastRow.option.propertyType);
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
    this.sanitizeScopedSelections();
    this.blueprintSearch.setValue('');
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
  }

  protected removeBlueprint(blueprintId: number): void {
    this.selectedBlueprintIds.update((current) => current.filter((candidate) => candidate !== blueprintId));
    this.sanitizeScopedSelections();
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

  protected addPropertyFilterRow(): void {
    this.propertyFilterRows.update((current) => [
      ...current,
      {
        id: crypto.randomUUID(),
        optionKey: null,
        value: '',
      },
    ]);
  }

  protected removePropertyFilterRow(rowId: string): void {
    this.propertyFilterRows.update((current) => current.filter((row) => row.id !== rowId));
  }

  protected updatePropertyOption(rowId: string, optionKey: string | null): void {
    this.propertyFilterRows.update((current) => current.map((row) =>
      row.id === rowId
        ? { ...row, optionKey }
        : row));
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
    this.quickRange.set('15m');
    this.createdFromInput.set(this.defaultCreatedFromValue());
    this.createdToInput.set(this.defaultCreatedToValue());
    this.blueprintSearch.setValue('');
    this.nodeSearch.setValue('');
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
      processStatuses: [...this.selectedProcessStatuses()],
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

  private restoreInitialRequest(initialRequest: ProcessSearchRequest | null): void {
    if (!initialRequest) {
      this.resetAll();
      return;
    }

    this.selectedBlueprintIds.set([...initialRequest.blueprintDefinitionIds].sort((left, right) => left - right));
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

    this.propertyFilterRows.set(initialRequest.propertyFilters.map((filter) => ({
      id: crypto.randomUUID(),
      optionKey: propertyLookup.get(this.propertyKey(filter)) ?? null,
      value: filter.value,
    })));
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
          const taskDefinition = taskDefinitionsById.get(task.taskDefinitionId);
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
          const taskDefinition = taskDefinitionsById.get(task.taskDefinitionId);
          const taskVersion = taskDefinition
            ? this.blueprintEditor.resolveVersionForLane(taskDefinition, task.allowedMajor, task.allowedMinor, task.resolvedVersionId)
            : null;
          const nodeKey = `${blueprint.id}:${task.instanceId}`;
          const taskName = taskDefinition?.name ?? `Task ${task.taskDefinitionId}`;

          for (const property of normalizeTaskPropertyDefinitions(taskVersion?.requiredGlobalState?.properties)) {
            const option: PropertyCatalogItem = {
              key: `global:${blueprint.id}:${property.name}:${property.kind}`,
              scope: 'GlobalState',
              blueprintDefinitionId: blueprint.id,
              blueprintNodeId: null,
              propertyKey: property.name,
              propertyType: property.kind,
              enumValues: [...property.enumValues],
              label: `Global / ${property.name}`,
              subtitle: blueprint.name,
              searchText: `${blueprint.name} global ${property.name}`.toLocaleLowerCase(),
            };
            propertyMap.set(option.key, option);
          }

          if (!nodeScopeKeys.has(nodeKey)) {
            continue;
          }

          for (const property of normalizeTaskPropertyDefinitions(taskVersion?.inputChannel?.properties)) {
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

    const validPropertyKeys = new Set(this.propertyCatalog().map((option) => option.key));
    this.propertyFilterRows.update((current) =>
      current.filter((row) => row.optionKey === null || validPropertyKeys.has(row.optionKey)));
  }
}
