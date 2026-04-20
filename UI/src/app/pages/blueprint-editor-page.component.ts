import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';
import { BlueprintEditorService, BlueprintViewModel } from '../domain/blueprint-editor.service';
import { BlueprintEditorStore } from '../domain/blueprint-editor.store';
import { BuilderInputService } from '../domain/builder-input.service';
import { TaskDefinitionsService } from '../domain/task-definitions.service';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';
import { WorkflowCanvasComponent } from '../components/workflow-canvas.component';

interface SidebarShortcut {
  icon: string;
  label: string;
}

interface TaskLibraryItem {
  task: ITaskDefinition;
  icon: string;
  description: string;
}

interface TaskLibrarySection {
  title: string;
  items: TaskLibraryItem[];
}

const BLUEPRINT_SHORTCUTS: SidebarShortcut[] = [
  { icon: 'share', label: 'Payment Retry' },
  { icon: 'auto_awesome', label: 'Notification Sync' },
  { icon: 'inventory_2', label: 'Archive' },
  { icon: 'delete', label: 'Trash' },
];

const SUPPORT_LINKS: SidebarShortcut[] = [
  { icon: 'menu_book', label: 'Docs' },
  { icon: 'help', label: 'Support' },
];

@Component({
  selector: 'app-blueprint-editor-page',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatIconModule,
    WorkflowCanvasComponent,
  ],
  templateUrl: './blueprint-editor-page.component.html',
  styleUrl: './blueprint-editor-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BlueprintEditorPageComponent {
  protected readonly Math = Math;
  protected readonly blueprintShortcuts = BLUEPRINT_SHORTCUTS;
  protected readonly supportLinks = SUPPORT_LINKS;

  protected readonly editor = inject(BlueprintEditorService);
  protected readonly editorStore = inject(BlueprintEditorStore);
  private readonly taskDefinitions = inject(TaskDefinitionsService);
  private readonly builderInput = inject(BuilderInputService);
  private readonly canvasViewport = viewChild(WorkflowCanvasComponent);
  private readonly gridSize = 20;

  protected readonly saving = signal(false);
  protected readonly blueprints = signal<BlueprintViewModel[]>([]);
  protected readonly selectedBlueprintId = signal<number | null>(null);
  protected readonly drawerCollapsed = signal(false);
  protected readonly tasks = toSignal(this.taskDefinitions.getTaskDefinitions(), { initialValue: [] as ITaskDefinition[] });
  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
  protected readonly zoomPercent = computed(() => this.canvasViewport()?.zoomPercent() ?? 100);
  protected readonly filteredBlueprints = computed(() =>
    this.filterBlueprints(this.blueprints(), this.blueprintSearch.value),
  );
  protected readonly taskLibrarySections = computed(() =>
    this.groupTaskDefinitions(this.tasks()),
  );
  protected readonly selectedBlueprintName = computed(() => {
    const selectedId = this.selectedBlueprintId();
    const match = this.blueprints().find((blueprint) => blueprint.id === selectedId);
    return match?.name ?? 'User Onboarding';
  });
  protected readonly activeNodeCount = computed(() => this.editorStore.nodes().length);
  protected readonly activeChannelCount = computed(() => this.editorStore.connections().length);
  protected readonly availableChannelCount = computed(() =>
    Math.max(this.tasks().length - this.activeChannelCount(), 0),
  );
  protected readonly canUndo = this.editorStore.canUndo;
  protected readonly canCopy = this.editorStore.canCopy;
  protected readonly canPaste = this.editorStore.canPaste;
  protected readonly canDelete = this.editorStore.canDelete;
  protected readonly projectInitial = computed(() =>
    this.selectedBlueprintName().trim().charAt(0).toUpperCase() || 'A',
  );

  constructor() {
    void this.builderInput;
    void this.refreshBlueprints();
  }

  protected async save(): Promise<void> {
    const nodes = this.editorStore.nodes();
    if (nodes.length === 0) {
      return;
    }

    const connections = this.editorStore.connections();
    const destinationNodeIds = new Set(connections.map((connection) => connection.to.id));
    const rootNodes = nodes.filter((node) => !destinationNodeIds.has(node.id));
    const startingNodeId = rootNodes[0]?.id ?? nodes[0].id;
    const blueprintName = `Blueprint ${new Date().toISOString()}`;

    this.saving.set(true);
    try {
      const created = await firstValueFrom(this.editor.saveBlueprintGraph(
        blueprintName,
        startingNodeId,
        this.tasks(),
        nodes,
        connections,
      ));
      this.selectedBlueprintId.set(created.id);
      await this.refreshBlueprints();
    } finally {
      this.saving.set(false);
    }
  }

  protected async openBlueprint(blueprintId: number): Promise<void> {
    const graph = await firstValueFrom(this.editor.loadBlueprintGraph(blueprintId, this.tasks()));
    this.editorStore.loadGraph(graph.nodes, graph.connections);
    this.selectedBlueprintId.set(graph.blueprint.id);
    requestAnimationFrame(() => {
      this.canvasViewport()?.resetView();
      this.canvasViewport()?.focusCanvas();
    });
  }

  protected newBlueprint(): void {
    this.selectedBlueprintId.set(null);
    this.editorStore.clearGraph();
    requestAnimationFrame(() => this.canvasViewport()?.focusCanvas());
  }

  protected toggleDrawer(): void {
    this.drawerCollapsed.update((collapsed) => !collapsed);
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
    requestAnimationFrame(() => {
      this.canvasViewport()?.centerOnNode(node.id);
      this.canvasViewport()?.focusCanvas();
    });
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

  private groupTaskDefinitions(tasks: ITaskDefinition[]): TaskLibrarySection[] {
    const sections = new Map<string, TaskLibraryItem[]>();

    for (const task of tasks) {
      const section = this.getTaskSection(task);
      const current = sections.get(section) ?? [];
      current.push({
        task,
        icon: this.getTaskIcon(section, task),
        description:
          task.versions[0]?.description?.trim() ||
          'Add workflow plugin to canvas',
      });
      sections.set(section, current);
    }

    return Array.from(sections.entries()).map(([title, items]) => ({
      title,
      items: items.sort((a, b) => a.task.name.localeCompare(b.task.name)),
    }));
  }

  private getTaskSection(task: ITaskDefinition): string {
    const version = task.versions[0];
    if (!version) {
      return 'Actions';
    }

    if (!version.inputChannel) {
      return 'Triggers';
    }

    if (version.outputChannels.length > 1) {
      return 'Logic';
    }

    return 'Actions';
  }

  private getTaskIcon(section: string, task: ITaskDefinition): string {
    const normalizedName = task.name.toLocaleLowerCase();

    if (normalizedName.includes('email') || normalizedName.includes('mail')) return 'mail';
    if (normalizedName.includes('schedule') || normalizedName.includes('timer')) return 'schedule';
    if (normalizedName.includes('webhook') || normalizedName.includes('http')) return 'bolt';
    if (normalizedName.includes('database') || normalizedName.includes('sql')) return 'storage';
    if (normalizedName.includes('condition') || normalizedName.includes('switch')) return 'account_tree';
    if (section === 'Triggers') return 'play_arrow';
    if (section === 'Logic') return 'alt_route';
    return 'extension';
  }
}
