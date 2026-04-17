import {
    AfterViewInit,
    ChangeDetectionStrategy,
    Component,
    computed,
    effect,
    ElementRef,
    inject,
    Injector,
    OnDestroy,
    runInInjectionContext,
    Signal,
    signal,
    viewChild,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { BlueprintEditorService, BlueprintViewModel } from '../domain/blueprint-editor.service';
import { CanvasWrapper } from '../domain/canvas-wrapper';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';
import { NodeAnchor, NodeModel, Point, Side } from '../domain/node-model';

interface DragNode {
    id: number;
    dx: number;
    dy: number;
}

interface AppNavItem {
    label: string;
    route?: string;
    exact?: boolean;
    disabled?: boolean;
}

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

const APP_NAV_ITEMS: AppNavItem[] = [
    { label: 'Workflows', route: '/blueprints', exact: true },
    { label: 'Trigger', route: '/execute' },
    { label: 'Executions', route: '/executions' },
];

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
        RouterLink,
        RouterLinkActive,
        MatToolbarModule,
        MatButtonModule,
        MatSidenavModule,
        MatListModule,
        MatIconModule,
    ],
    templateUrl: './blueprint-editor-page.component.html',
    styleUrl: './blueprint-editor-page.component.css',
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BlueprintEditorPageComponent implements AfterViewInit, OnDestroy {
    protected readonly Math = Math;
    protected readonly appNavItems = APP_NAV_ITEMS;
    protected readonly canvasActions = CANVAS_ACTIONS;
    protected readonly blueprintShortcuts = BLUEPRINT_SHORTCUTS;
    protected readonly supportLinks = SUPPORT_LINKS;
    private readonly injector = inject(Injector);
    private readonly http = inject(HttpClient);
    private readonly editor = inject(BlueprintEditorService);

    private readonly canvasRef =
        viewChild.required<ElementRef<HTMLCanvasElement>>('myCanvas');

    private get canvas(): HTMLCanvasElement {
        return this.canvasRef().nativeElement;
    }

    get zoom(): Signal<number> {
        return computed(() => this.canvasWrapper?.zoomLevel() ?? 1);
    }

    private canvasWrapper?: CanvasWrapper;
    private readonly gridSize = 20;
    protected readonly dragOffset = signal<Point | null>(null);
    protected readonly dragNode = signal<DragNode | null>(null);
    private animationId: number | null = null;
    private zoomRedrawTimeoutId: number | null = null;

    protected readonly hoveredNodeId = signal<number | null>(null);
    protected readonly hoveredAnchor = signal<number | null>(null);

    protected readonly dragConnection = signal<{
        from: number;
        anchor: number;
        x: number;
        y: number;
        side?: Side;
    } | null>(null);

    protected readonly saving = signal(false);
    protected readonly blueprints = signal<BlueprintViewModel[]>([]);
    protected readonly selectedBlueprintId = signal<number | null>(null);
    protected readonly drawerCollapsed = signal(false);

    protected readonly tasks = signal<ITaskDefinition[]>([]);
    protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
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

    private resizeObserver?: ResizeObserver;

    ngOnDestroy(): void {
        this.resizeObserver?.disconnect();
        if (this.zoomRedrawTimeoutId !== null) {
            window.clearTimeout(this.zoomRedrawTimeoutId);
            this.zoomRedrawTimeoutId = null;
        }
    }

    private readonly onResize = () => {
        this.syncCanvasBackingSize();
        this.draw();
    };

    private syncCanvasBackingSize(): void {
        const el = this.canvasRef().nativeElement;
        const dpr = window.devicePixelRatio || 1;
        const cssWidth = Math.max(1, Math.floor(el.clientWidth));
        const cssHeight = Math.max(1, Math.floor(el.clientHeight));
        const width = Math.max(1, Math.floor(cssWidth * dpr));
        const height = Math.max(1, Math.floor(cssHeight * dpr));

        if (el.width !== width) el.width = width;
        if (el.height !== height) el.height = height;

        const ctx = this.canvasWrapper?.ctx;
        if (ctx) {
            ctx.setTransform(1, 0, 0, 1, 0, 0);
            ctx.scale(dpr, dpr);
        }
    }

    ngAfterViewInit(): void {
        const c = this.canvasRef();
        if (!c) return;
        const ctx = c.nativeElement.getContext('2d')!;
        this.canvasWrapper = new CanvasWrapper(ctx, this.gridSize);
        this.syncCanvasBackingSize();

        this.resizeObserver = new ResizeObserver(() => this.onResize());
        this.resizeObserver.observe(c.nativeElement);

        this.http.get<ITaskDefinition[]>('tasks').subscribe((t) => {
            this.tasks.set(t);
        });
        void this.refreshBlueprints();
        runInInjectionContext(this.injector, () => {
            effect(() => {
                this.editor.nodes();
                this.editor.connections();
                this.canvasWrapper?.fontReady();
                this.hoveredNodeId();
                this.hoveredAnchor();
                this.dragConnection();
                this.dragNode();
                this.dragOffset();
                this.draw();
            });
        });
    }

    protected async save(): Promise<void> {
        const nodes = this.editor.nodes();
        if (nodes.length === 0) return;

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
        if (!task.versions[0]?.id || !this.canvasWrapper) {
            return;
        }

        const offset = this.canvasWrapper.viewportOffset();
        const zoom = this.canvasWrapper.zoomLevel();
        const stagger = (this.editor.nodes().length % 5) * this.gridSize * 2;
        const coordinates = {
            x: (this.canvas.clientWidth * 0.5 - offset.x) / zoom + stagger,
            y: (this.canvas.clientHeight * 0.5 - offset.y) / zoom + stagger * 0.45,
        };

        this.editor.createNode(
            task,
            coordinates,
            (v) => this.canvasWrapper!.snap(v),
        );
    }

    private async refreshBlueprints(): Promise<void> {
        const blueprints = await this.editor.getBlueprints();
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

    private getMousePos(event: MouseEvent): { x: number; y: number } {
        const rect = this.canvas.getBoundingClientRect();
        return { x: event.clientX - rect.left, y: event.clientY - rect.top };
    }

    private getWorldCoordinates(event: MouseEvent): {
        canvas: { x: number; y: number };
        world: { x: number; y: number };
    } {
        const canvasPos = this.getMousePos(event);
        const offset = this.canvasWrapper!.viewportOffset();
        const zoom = this.canvasWrapper!.zoomLevel();
        return {
            canvas: canvasPos,
            world: {
                x: (canvasPos.x - offset.x) / zoom,
                y: (canvasPos.y - offset.y) / zoom,
            },
        };
    }

    protected onMouseMove(event: MouseEvent): void {
        const wrapper = this.canvasWrapper!;
        const { canvas, world } = this.getWorldCoordinates(event);
        const { x, y } = canvas;
        const { x: worldX, y: worldY } = world;
        if (this.dragConnection()) {
            const drag = this.dragConnection()!;
            const target = this.editor.getNodeByCoordinates(
                { x: worldX, y: worldY },
                ...this.editor.getNodesConnectedTo(this.editor.getNodeById(drag.from)),
            );

            if (target) {
                const snap = target.inputAnchorSnap();
                if (snap) {
                    this.dragConnection.update((d) =>
                        d ? { ...d, x: snap.x, y: snap.y, side: 'left' } : d,
                    );
                    this.hoveredNodeId.set(target.id);
                    this.hoveredAnchor.set(target.inputAnchorIndex());
                } else {
                    this.dragConnection.update((d) =>
                        d ? { ...d, x: worldX, y: worldY, side: undefined } : d,
                    );
                    this.hoveredNodeId.set(null);
                    this.hoveredAnchor.set(null);
                }
            } else {
                this.dragConnection.update((d) =>
                    d ? { ...d, x: worldX, y: worldY, side: undefined } : d,
                );
                this.hoveredNodeId.set(null);
                this.hoveredAnchor.set(null);
            }
            return;
        }
        if (this.dragNode()) {
            const drag = this.dragNode()!;

            this.editor.updateNodeCoordinates(drag.id, {
                x: worldX - drag.dx,
                y: worldY - drag.dy,
            }, (v) => wrapper.snap(v));

            return;
        }
        if (this.dragOffset()) {
            const last = this.dragOffset()!;
            wrapper.viewportOffset.update((o) => ({
                x: o.x + (x - last.x),
                y: o.y + (y - last.y),
            }));
            this.dragOffset.set({ x, y });
            return;
        }
        const node = this.editor.getNodeByCoordinates({ x: worldX, y: worldY });
        this.hoveredNodeId.set(node ? node.id : null);

        if (node) {
            const idx = this.inAnchorCircle(worldX, worldY, node);
            this.hoveredAnchor.set(idx);
        } else {
            this.hoveredAnchor.set(null);
        }
    }

    protected onMouseDown(event: MouseEvent): void {
        const { canvas, world } = this.getWorldCoordinates(event);
        const { x, y } = canvas;
        const { x: worldX, y: worldY } = world;

        const hovered = this.editor.getNodeByCoordinates({ x: worldX, y: worldY });

        if (hovered) {
            const anchor = this.inAnchorCircle(worldX, worldY, hovered);
            if (anchor !== null && hovered.isOutputAnchor(anchor)) {
                this.dragConnection.set({
                    from: hovered.id,
                    anchor,
                    x: worldX,
                    y: worldY,
                    side: undefined,
                });
                this.startAnimation();
            } else {
                this.dragNode.set({
                    id: hovered.id,
                    dx: worldX - hovered.x,
                    dy: worldY - hovered.y,
                });
                this.startAnimation();
            }
            this.hoveredNodeId.set(hovered.id);
            this.hoveredAnchor.set(anchor);
        } else {
            this.dragOffset.set({ x, y });
            this.startAnimation();
        }
    }

    protected onMouseUp(event: MouseEvent): void {
        const wrapper = this.canvasWrapper!;
        const { world } = this.getWorldCoordinates(event);
        const { x: worldX, y: worldY } = world;

        const drag = this.dragConnection();
        if (drag) {
            const target = this.editor.getNodeByCoordinates(
                { x: worldX, y: worldY },
                ...this.editor.getNodesConnectedTo(this.editor.getNodeById(drag.from)),
            );

            if (target) {
                const snap = target.inputAnchorSnap();
                if (snap) {
                    this.editor.createConnection(
                        drag.from,
                        target.id,
                        drag.anchor,
                        'left',
                        snap.pos,
                        (v) => wrapper.snap(v),
                    );
                }
            }
            this.dragConnection.set(null);
            this.hoveredAnchor.set(null);
            this.hoveredNodeId.set(null);
        }

        if (this.dragNode()) {
            this.dragNode.set(null);
        }

        if (this.dragOffset()) {
            this.dragOffset.set(null);
        }

        this.stopAnimation();
    }

    protected onMouseLeave(): void {
        this.dragConnection.set(null);
        this.dragNode.set(null);
        this.dragOffset.set(null);
        this.hoveredNodeId.set(null);
        this.hoveredAnchor.set(null);
        this.stopAnimation();
    }

    protected onWheel(event: WheelEvent): void {
        event.preventDefault();
        const delta = Math.sign(event.deltaY);
        const z = this.canvasWrapper!.zoomLevel();
        let nz = z - delta * 0.05;
        nz = Math.min(3, Math.max(0.05, nz));
        this.canvasWrapper!.zoomLevel.set(parseFloat(nz.toFixed(2)));

        this.startAnimation();
        if (this.zoomRedrawTimeoutId !== null) {
            window.clearTimeout(this.zoomRedrawTimeoutId);
        }
        this.zoomRedrawTimeoutId = window.setTimeout(() => {
            this.zoomRedrawTimeoutId = null;
            if (!this.dragNode() && !this.dragConnection() && !this.dragOffset()) {
                this.stopAnimation();
            }
        }, 120);
    }

    protected resetView(): void {
        this.canvasWrapper!.zoomLevel.set(1);
        this.canvasWrapper!.viewportOffset.set({ x: 0, y: 0 });
    }

    private anchorPositions(node: NodeModel): Point[] {
        return node.anchorPositions();
    }

    private anchorDefinition(node: NodeModel, idx: number): NodeAnchor | null {
        return node.anchorDefinition(idx);
    }

    private inAnchorCircle(x: number, y: number, node: NodeModel): number | null {
        return node.inAnchorCircle(x, y);
    }

    private startAnimation(): void {
        if (this.animationId !== null) return;
        const step = () => {
            this.draw();
            this.animationId = requestAnimationFrame(step);
        };
        this.animationId = requestAnimationFrame(step);
    }

    private stopAnimation(): void {
        if (this.animationId !== null) {
            cancelAnimationFrame(this.animationId);
            this.animationId = null;
        }
    }

    private drawOrthogonalLine(
        sx: number,
        sy: number,
        ex: number,
        ey: number,
        obstacles: NodeModel[],
        side?: Side,
        destNode?: NodeModel,
        startSide?: Side,
        startNode?: NodeModel,
    ): void {
        this.canvasWrapper!.drawOrthogonalLine(
            sx,
            sy,
            ex,
            ey,
            obstacles,
            side,
            destNode,
            startSide,
            startNode,
        );
    }

    private draw(): void {
        const wrapper = this.canvasWrapper;
        const canvas = this.canvas;
        if (!wrapper || !canvas) return;
        const ctx = wrapper.ctx;

        const nodes = this.editor.nodes();
        const connections = this.editor.connections();

        wrapper.clear(canvas.width, canvas.height);

        ctx.save();
        const zoom = this.canvasWrapper!.zoomLevel();
        ctx.scale(zoom, zoom);
        const offset = this.canvasWrapper!.viewportOffset();
        ctx.translate(offset.x, offset.y);

        this.canvasWrapper!.drawConnections(connections, nodes);

        const hovered = this.hoveredNodeId();
        const drag = this.dragConnection();

        wrapper.drawNodes(nodes, connections, drag, hovered, this.hoveredAnchor());

        if (drag) {
            const fromNode = this.editor.getNodeById(drag.from);
            if (!fromNode.contains(drag.x, drag.y)) {
                const start = this.anchorPositions(fromNode)[drag.anchor];
                const hoveredNode =
                    hovered && hovered !== drag.from
                        ? this.editor.getNodeById(hovered)
                        : undefined;
                const obstacles = this.editor.getNodes(fromNode.id, hoveredNode?.id);

                this.drawOrthogonalLine(
                    start.x,
                    start.y,
                    drag.x,
                    drag.y,
                    obstacles,
                    drag.side ?? this.anchorDefinition(fromNode, drag.anchor)?.side,
                    hoveredNode,
                    this.anchorDefinition(fromNode, drag.anchor)?.side,
                    fromNode,
                );
            }
        }

        ctx.restore();
    }
}
