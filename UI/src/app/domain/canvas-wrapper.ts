import { signal } from '@angular/core';
import { NodeModel, Point, Side } from './node-model';
import { PathfinderOptions, orthogonalSegments } from './pathfinder';
import { ConnectionModel } from './connection-model';

export class CanvasWrapper {

    readonly nodes = signal<NodeModel[]>([]);
    readonly connections = signal<ConnectionModel[]>([]);
    readonly viewportOffset = signal<Point>({ x: 0, y: 0 });
    readonly zoomLevel = signal(1);

    readonly arrowSize = 8;
    constructor(
        public readonly ctx: CanvasRenderingContext2D,
        private readonly gridSize: number
    ) { }

    clear(width: number, height: number): void {
        this.ctx.clearRect(0, 0, width, height);
        this.ctx.strokeStyle = 'blue';
        this.ctx.fillStyle = 'blue';
        this.ctx.lineWidth = 2;
    }

    createNode(name: string, coordinates: Point): void {
        const nodes = [...this.nodes()];
        nodes.push(
            new NodeModel(
                Date.now(),
                name,
                this.snap(coordinates.x),
                this.snap(coordinates.y),
                240,
                160,
            ),
        );
        this.nodes.set(nodes);
    }

    createConnection(sourceNodeId: number, destinationNodeId: number, anchor: number, side: Side, position: number): void {
        const connections = [...this.connections()];
        const sourceNode = this.getNodeById(sourceNodeId);
        const destinationNode = this.getNodeById(destinationNodeId);
        const snappedPosition = this.snap(position);
        connections.push(
            new ConnectionModel(sourceNode, destinationNode, anchor, side, snappedPosition),
        );
        this.connections.set(connections);
    }

    getNodeById(id: number): NodeModel {
        return this.nodes().find((n) => n.id === id)!;
    }

    getNodes(...except: Array<number | undefined>): NodeModel[] {
        return this.nodes().filter(node => !except.includes(node.id));
    }

    getNodeByCoordinates(coordinates: Point, ...except: NodeModel[]): NodeModel | undefined {
        return this.nodes().find(node => {
            if (except.includes(node) ||
                except.some(n => n.contains(coordinates.x, coordinates.y))) {
                return false;
            }

            return node.contains(coordinates.x, coordinates.y);
        });
    }

    getNodesConnectedTo(node: NodeModel): NodeModel[] {
        return [...new Set(this.connections().filter(c => c.from === node || c.to === node)
            .map(c => [c.from, c.to])
            .flat()
            .filter(n => !!n))];
    }

    drawConnections(): void {
      for (const conn of this.connections()) {
            const obstacles = this.nodes().filter(
                (n) => n.id !== conn.from.id && n.id !== conn.to.id,
            );
            conn.draw(this, obstacles);
        }
    }

    drawNodes(drag: {
          from: number;
          anchor: number;
          x: number;
          y: number;
          side?: Side;
      } | null,
      hovered: number | null,
      hoveredAnchor: number | null) {
      for (const node of this.nodes()) {
            const isHoveredDest =
                drag && node.id !== drag.from && hovered === node.id;
            const eligible =
                drag &&
                node.id !== drag.from &&
                !this.connections().some(
                    (c) =>
                        (c.from.id === drag.from && c.to.id === node.id) ||
                        (c.to.id === drag.from && c.from.id === node.id),
                );

            let saved = false;
            if (isHoveredDest) {
                this.ctx.save();
                this.ctx.strokeStyle = 'rgba(0,0,255,0.5)';
                this.ctx.setLineDash([4, 4]);
                saved = true;
            } else if (eligible) {
                this.ctx.save();
                this.ctx.globalAlpha = 0.3;
                saved = true;
            }

            this.ctx.strokeRect(node.x, node.y, node.width, node.height);
            this.drawWrappedText(
                node.name,
                node.x + 10,
                node.y + 20,
                node.width - 20,
            );

            if (isHoveredDest) {
                this.ctx.restore();
            } else if (saved) {
                this.ctx.restore();
            }

            const anchors = node.anchorPositions();
            if (drag && drag.from === node.id) {
                const pos = anchors[drag.anchor];
                this.ctx.beginPath();
                this.ctx.arc(pos.x, pos.y, node.anchorRadius(drag.anchor), 0, 2 * Math.PI);
                this.ctx.fill();
            } else if (!drag && hovered === node.id) {
                for (let i = 0; i < anchors.length; i++) {
                    this.ctx.beginPath();
                    this.ctx.globalAlpha = hoveredAnchor === i ? 1 : 0.3;
                    this.ctx.arc(
                        anchors[i].x,
                        anchors[i].y,
                        node.anchorRadius(i),
                        0,
                        2 * Math.PI,
                    );
                    this.ctx.fill();
                    this.ctx.globalAlpha = 1;
                }
            }
        }
    }

    updateNodeCoordinates(nodeId: number, newCoordinates: Point): void {
        const node = this.getNodeById(nodeId);
        node.x = this.snap(newCoordinates.x);
        node.y = this.snap(newCoordinates.y);
    }

    snap(value: number): number {
        return Math.round(value / this.gridSize) * this.gridSize;
    }

    drawWrappedText(text: string, x: number, y: number, maxWidth: number): void {
        const words = text.split(/\s+/);
        let line = '';
        const lineHeight = 16;
        for (const word of words) {
            const test = line ? `${line} ${word}` : word;
            if (this.ctx.measureText(test).width > maxWidth && line) {
                this.ctx.fillText(line, x, y);
                line = word;
                y += lineHeight;
            } else {
                line = test;
            }
        }
        this.ctx.fillText(line, x, y);
    }

    drawArrowHead(x: number, y: number, side: Side): void {
        const size = this.arrowSize;
        x = this.snap(x);
        y = this.snap(y);
        this.ctx.beginPath();
        switch (side) {
            case 'left':
                this.ctx.moveTo(x, y);
                this.ctx.lineTo(x - size, y - size / 2);
                this.ctx.lineTo(x - size, y + size / 2);
                break;
            case 'right':
                this.ctx.moveTo(x, y);
                this.ctx.lineTo(x + size, y - size / 2);
                this.ctx.lineTo(x + size, y + size / 2);
                break;
            case 'top':
                this.ctx.moveTo(x, y);
                this.ctx.lineTo(x - size / 2, y - size);
                this.ctx.lineTo(x + size / 2, y - size);
                break;
            default:
                this.ctx.moveTo(x, y);
                this.ctx.lineTo(x - size / 2, y + size);
                this.ctx.lineTo(x + size / 2, y + size);
        }
        this.ctx.closePath();
        this.ctx.fill();
    }

    private drawSegment(x1: number, y1: number, x2: number, y2: number): void {
        const t = this.ctx.lineWidth;
        if (x1 === x2) {
            const top = Math.min(y1, y2);
            this.ctx.fillRect(x1 - t / 2, top, t, Math.abs(y2 - y1));
        } else if (y1 === y2) {
            const left = Math.min(x1, x2);
            this.ctx.fillRect(left, y1 - t / 2, Math.abs(x2 - x1), t);
        }
    }

    drawOrthogonalLine(
        sx: number,
        sy: number,
        ex: number,
        ey: number,
        obstacles: NodeModel[] = [],
        side?: Side,
        destNode?: NodeModel,
        startSide?: Side,
        startNode?: NodeModel,
    ): void {
        const options: PathfinderOptions = {
            gridSize: this.gridSize,
            nodeMargin: NodeModel.buffer,
            obstacleBuffer: Math.max(NodeModel.buffer / this.gridSize, 1),
        };
        const segments = orthogonalSegments(
            sx,
            sy,
            ex,
            ey,
            options,
            obstacles,
            side,
            destNode,
            startSide,
            startNode,
        );
        for (const seg of segments) {
            this.drawSegment(seg.x1, seg.y1, seg.x2, seg.y2);
        }
    }
}
