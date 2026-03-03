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
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { CanvasWrapper } from './domain/canvas-wrapper';
import { ConnectionModel } from './domain/connection-model';
import { NodeModel, Point, Side } from './domain/node-model';
import { Task } from './domain/task';
import { GlobalSignals } from './domain/global-signals';
import { ITaskDefinition } from './interfaces/ITaskDefinition';
import { BlueprintEditorService } from './domain/blueprint-editor.service';

interface DragNode {
    id: number;
    dx: number;
    dy: number;
}

@Component({
    selector: 'app-root',
    imports: [
        ReactiveFormsModule,
        MatToolbarModule,
        MatButtonModule,
        MatSidenavModule,
        MatListModule,
        MatIconModule,
    ],
    templateUrl: './app.html',
    styleUrl: './app.css',
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App implements AfterViewInit, OnDestroy {
    protected readonly title = signal('workflow-builder');
    protected readonly Math = Math;
    private readonly injector = inject(Injector);

    private readonly canvasRef =
        viewChild.required<ElementRef<HTMLCanvasElement>>('myCanvas');

    private readonly selectRef =
        viewChild<ElementRef<HTMLSelectElement>>('taskSelect');

    private get canvas(): HTMLCanvasElement {
        return this.canvasRef().nativeElement;
    }

    ngOnDestroy(): void {
        this.resizeObserver?.disconnect();
        if (this.zoomRedrawTimeoutId !== null) {
            window.clearTimeout(this.zoomRedrawTimeoutId);
            this.zoomRedrawTimeoutId = null;
        }
    }

    get zoom(): Signal<number> {
        return computed(() => this.canvasWrapper?.zoomLevel() ?? 1);
    }

    private canvasWrapper?: CanvasWrapper;
    private globalSignals?: GlobalSignals;

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

    protected readonly showModal = signal(false);
    private readonly newNodePos = signal<{ x: number; y: number } | null>(null);

    protected readonly tasks = signal<ITaskDefinition[]>([]);
    protected readonly selectedTask = new FormControl('', { nonNullable: true });
    private readonly http = inject(HttpClient);
    private readonly editor = inject(BlueprintEditorService);

    private resizeObserver?: ResizeObserver;

    private readonly onResize = () => {
        this.syncCanvasBackingSize();
        this.draw();
    };

    private syncCanvasBackingSize(): void {
        const el = this.canvasRef().nativeElement;

        // Keep the backing buffer in sync with the canvas' rendered size to avoid warping.
        // This does not change world coordinates; it only adjusts pixel density.
        const dpr = window.devicePixelRatio || 1;
        const cssWidth = Math.max(1, Math.floor(el.clientWidth));
        const cssHeight = Math.max(1, Math.floor(el.clientHeight));
        const width = Math.max(1, Math.floor(cssWidth * dpr));
        const height = Math.max(1, Math.floor(cssHeight * dpr));

        if (el.width !== width) el.width = width;
        if (el.height !== height) el.height = height;

        // Map 1 canvas unit == 1 CSS pixel, so our existing drawing math stays correct.
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
            if (t.length > 0) {
                this.selectedTask.setValue(t[0].name);
            }
        });
        runInInjectionContext(this.injector, () => {
            effect(() => {
                // Track blueprint/editor + interaction signals so the canvas redraws
                // when nodes/connections or hover/drag state changes.
                this.editor.nodes();
                this.editor.connections();
                this.hoveredNodeId();
                this.hoveredAnchor();
                this.dragConnection();
                this.dragNode();
                this.dragOffset();
                this.draw();
            });
        });
    }

    protected openModal(event: MouseEvent): void {
        const rect = this.canvas!.getBoundingClientRect();
        const canvasPos = {
            x: event.clientX - rect.left,
            y: event.clientY - rect.top,
        };
        const off = this.canvasWrapper!.viewportOffset();
        const zoom = this.canvasWrapper!.zoomLevel();

        this.newNodePos.set({
            x: (canvasPos.x - off.x) / zoom,
            y: (canvasPos.y - off.y) / zoom,
        });

        const tasks = this.tasks();
        this.selectedTask.setValue(tasks[0]?.name ?? '');
        this.showModal.set(true);
        queueMicrotask(() => this.selectRef()?.nativeElement.focus());
    }

    protected createNode(): void {
        const name = this.selectedTask.value;
        const coordinates = this.newNodePos();
        if (!name || !coordinates) {
            this.showModal.set(false);
            return;
        }
        this.editor.createNode(name, coordinates, (v) => this.canvasWrapper!.snap(v));
        this.showModal.set(false);
    }

    private getMousePos(event: MouseEvent): { x: number; y: number } {
        const rect = this.canvas!.getBoundingClientRect();
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
            const from = this.editor.getNodeById(drag.from);
            const side = this.anchorSide(drag.anchor);
            // switch anchor if dragged beyond opposite side
            if (
                (side === 'left' && worldX > from.x + from.width) ||
                (side === 'right' && worldX < from.x) ||
                (side === 'top' && worldY > from.y + from.height) ||
                (side === 'bottom' && worldY < from.y)
            ) {
                const newAnchor = this.oppositeAnchor(drag.anchor);
                this.dragConnection.update((d) =>
                    d ? { ...d, anchor: newAnchor } : d,
                );
            }

            const target = this.editor.getNodeByCoordinates(
                { x: worldX, y: worldY },
                ...this.editor.getNodesConnectedTo(this.editor.getNodeById(drag.from)),
            );

            if (target) {
                const dir = this.sideFromPoint(worldX, worldY, target);
                const snap = this.snapPointFixedSide(worldX, worldY, target, dir);
                this.dragConnection.update((d) =>
                    d ? { ...d, x: snap.x, y: snap.y, side: dir } : d,
                );
                this.hoveredNodeId.set(target.id);
            } else {
                this.dragConnection.update((d) =>
                    d ? { ...d, x: worldX, y: worldY, side: undefined } : d,
                );
                this.hoveredNodeId.set(null);
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
        const wrapper = this.canvasWrapper!;
        const { canvas, world } = this.getWorldCoordinates(event);
        const { x, y } = canvas;
        const { x: worldX, y: worldY } = world;

        const hovered = this.editor.getNodeByCoordinates({ x: worldX, y: worldY });

        if (hovered) {
            const anchor = this.inAnchorCircle(worldX, worldY, hovered);
            if (anchor !== null) {
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
                const dir = this.sideFromPoint(worldX, worldY, target);
                const snap = this.snapPointFixedSide(worldX, worldY, target, dir);
                this.editor.createConnection(
                    drag.from,
                    target.id,
                    drag.anchor,
                    dir,
                    snap.pos,
                    (v) => wrapper.snap(v),
                );
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

    private anchorSide(idx: number): Side {
        return NodeModel.anchorSide(idx);
    }

    private oppositeAnchor(idx: number): number {
        return NodeModel.oppositeAnchor(idx);
    }

    private snapPointFixedSide(
        x: number,
        y: number,
        node: NodeModel,
        side: Side,
    ): { x: number; y: number; side: Side; pos: number } {
        return node.snapPointFixedSide(x, y, side);
    }

    private sideFromPoint(x: number, y: number, node: NodeModel): Side {
        return node.sideFromPoint(x, y);
    }

    private inAnchorCircle(x: number, y: number, node: NodeModel): number | null {
        const anchors = this.anchorPositions(node);
        for (let i = 0; i < anchors.length; i++) {
            if (
                Math.hypot(x - anchors[i].x, y - anchors[i].y) <= node.anchorRadius(i)
            ) {
                return i;
            }
        }
        return null;
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

        this.canvasWrapper?.drawNodes(nodes, connections, drag, hovered, this.hoveredAnchor());

        if (drag) {
            const fromNode = this.editor.getNodeById(drag.from);
            if (!fromNode.contains(drag.x, drag.y)) {
                const start = this.anchorPositions(fromNode)[drag.anchor];
                const hoveredNode =
                    hovered && hovered !== drag.from
                        ? this.editor.getNodeById(hovered)
                        : undefined;
                /*if (hoveredNode && drag.side) {
                  const position = this.snapPointFixedSide(
                    drag.x,
                    drag.y,
                    hoveredNode,
                    drag.side,
                  ).pos;
                  const obstacles = this.nodes().filter(
                    (n) => n.id !== from.id && n.id !== hoveredNode.id,
                  );
                  new ConnectionModel(
                    from,
                    hoveredNode,
                    drag.anchor,
                    drag.side,
                    position,
                  ).draw(wrapper, obstacles);
                } else {*/
                const obstacles = this.editor.getNodes(fromNode.id, hoveredNode?.id);

                this.drawOrthogonalLine(
                    start.x,
                    start.y,
                    drag.x,
                    drag.y,
                    obstacles,
                    drag.side ?? this.anchorSide(drag.anchor),
                    hoveredNode,
                    this.anchorSide(drag.anchor),
                    fromNode,
                );
                /*}*/
            }
        }

        ctx.restore();
    }
}
