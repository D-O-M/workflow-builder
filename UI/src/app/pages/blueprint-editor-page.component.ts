import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { BlueprintEditorService, BlueprintViewModel } from '../domain/blueprint-editor.service';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';
import { WorkflowCanvasComponent } from '../components/workflow-canvas.component';

interface QuickAction {
  icon: string;
  label: string;
  active?: boolean;
}

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

const CANVAS_ACTIONS: QuickAction[] = [
  { icon: 'pan_tool_alt', label: 'Pan', active: true },
  { icon: 'back_hand', label: 'Move' },
  { icon: 'add_box', label: 'Add' },
  { icon: 'history', label: 'History' },
];

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
  protected readonly canvasActions = CANVAS_ACTIONS;
  protected readonly blueprintShortcuts = BLUEPRINT_SHORTCUTS;
  protected readonly supportLinks = SUPPORT_LINKS;

  private readonly http = inject(HttpClient);
  protected readonly editor = inject(BlueprintEditorService);
  private readonly canvasViewport = viewChild(WorkflowCanvasComponent);
  private readonly gridSize = 20;

  protected readonly saving = signal(false);
  protected readonly blueprints = signal<BlueprintViewModel[]>([]);
  protected readonly selectedBlueprintId = signal<number | null>(null);
  protected readonly drawerCollapsed = signal(false);
  protected readonly tasks = signal<ITaskDefinition[]>([]);
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
  protected readonly activeNodeCount = computed(() => this.editor.nodes().length);
  protected readonly activeChannelCount = computed(() => this.editor.connections().length);
  protected readonly availableChannelCount = computed(() =>
    Math.max(this.tasks().length - this.activeChannelCount(), 0),
  );
  protected readonly projectInitial = computed(() =>
    this.selectedBlueprintName().trim().charAt(0).toUpperCase() || 'A',
  );

  constructor() {
    this.http.get<ITaskDefinition[]>('tasks').subscribe((tasks) => {
      this.tasks.set(tasks);
    });
    void this.refreshBlueprints();
  }

  protected async save(): Promise<void> {
    const nodes = this.editor.nodes();
    if (nodes.length === 0) {
      return;
    }

    const destinationNodeIds = new Set(this.editor.connections().map((connection) => connection.to.id));
    const rootNodes = nodes.filter((node) => !destinationNodeIds.has(node.id));
    const startingNodeId = rootNodes[0]?.id ?? nodes[0].id;
    const blueprintName = `Blueprint ${new Date().toISOString()}`;

    this.saving.set(true);
    try {
      const created = await this.editor.saveBlueprint(blueprintName, startingNodeId, this.tasks());
      this.selectedBlueprintId.set(created.id);
      await this.refreshBlueprints();
    } finally {
      this.saving.set(false);
    }
  }

  protected async openBlueprint(blueprintId: number): Promise<void> {
    await this.editor.loadBlueprint(blueprintId, this.tasks());
    this.selectedBlueprintId.set(blueprintId);
  }

  protected newBlueprint(): void {
    this.selectedBlueprintId.set(null);
    this.editor.clear();
  }

  protected toggleDrawer(): void {
    this.drawerCollapsed.update((collapsed) => !collapsed);
  }

  protected addTaskFromDrawer(task: ITaskDefinition): void {
    if (!task.versions[0]?.id) {
      return;
    }

    const viewportCenter = this.canvasViewport()?.viewportCenterWorld() ?? { x: 420, y: 260 };
    const stagger = (this.editor.nodes().length % 5) * this.gridSize * 2;
    this.editor.createNode(
      task,
      {
        x: viewportCenter.x + stagger,
        y: viewportCenter.y + stagger * 0.45,
      },
      (value) => this.snap(value),
    );
  }

  protected onNodeMoved(event: { nodeId: number; coordinates: { x: number; y: number } }): void {
    this.editor.updateNodeCoordinates(event.nodeId, event.coordinates, (value) => value);
  }

  protected onConnectionCreated(event: {
    sourceNodeId: number;
    destinationNodeId: number;
    anchor: number;
    side: 'left' | 'right' | 'top' | 'bottom';
    position: number;
  }): void {
    this.editor.createConnection(
      event.sourceNodeId,
      event.destinationNodeId,
      event.anchor,
      event.side,
      event.position,
      (value) => value,
    );
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
    const blueprints = await this.editor.getBlueprints();
    this.blueprints.set(blueprints);
  }

  private snap(value: number): number {
    return Math.round(value / this.gridSize) * this.gridSize;
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
