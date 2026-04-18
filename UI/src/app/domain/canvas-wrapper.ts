import { NodeModel, Point, Side } from './node-model';
import { PathfinderOptions, orthogonalSegments } from './pathfinder';
import { ConnectionModel } from './connection-model';
import { signal } from '@angular/core';

export class CanvasWrapper {

    private static readonly theme = {
        surface: '#ffffff',
        surfaceAlt: '#f7f9ff',
        surfaceAccent: '#eef3ff',
        nodeStroke: 'rgba(85, 108, 176, 0.16)',
        nodeStrokeHover: '#2d4bdd',
        nodeText: '#101728',
        nodeTextMuted: 'rgba(113, 131, 166, 0.88)',
        nodeSectionText: 'rgba(136, 153, 187, 0.95)',
        nodeDanger: '#df3d3d',
        nodeWarning: '#f28c18',
        nodeWarningText: '#fff7ed',
        nodeShadow: 'rgba(57, 81, 153, 0.16)',
        accent: '#2d4bdd',
        tooltipBg: '#101728',
        tooltipText: '#ffffff',
        tooltipDanger: '#ffb8b8',
        anchor: {
            normal: '#d6e0ff',
            hover: '#2d4bdd',
            stroke: 'rgba(56, 76, 140, 0.22)',
            fill: '#ffffff',
            connected: '#1f49ff',
        },
        eligibleDash: 'rgba(45, 75, 221, 0.45)',
        connection: {
            stroke: '#2d4bdd',
            soft: 'rgba(45, 75, 221, 0.72)',
        },
        status: {
            Completed: {
                accent: '#12b76a',
                text: '#027a48',
                border: 'rgba(18, 183, 106, 0.3)',
                badgeFill: 'rgba(18, 183, 106, 0.14)',
            },
            InProgress: {
                accent: '#2d4bdd',
                text: '#2446c5',
                border: 'rgba(45, 75, 221, 0.24)',
                badgeFill: 'rgba(45, 75, 221, 0.12)',
            },
            Failed: {
                accent: '#d92d20',
                text: '#b42318',
                border: 'rgba(217, 45, 32, 0.3)',
                badgeFill: 'rgba(217, 45, 32, 0.12)',
            },
            Pending: {
                accent: '#64748b',
                text: '#475467',
                border: 'rgba(100, 116, 139, 0.28)',
                badgeFill: 'rgba(100, 116, 139, 0.12)',
            },
            NotExecuted: {
                accent: '#cbd5e1',
                text: '#667085',
                border: 'rgba(203, 213, 225, 0.42)',
                badgeFill: 'rgba(203, 213, 225, 0.26)',
            },
        },
    } as const;

    readonly viewportOffset = signal<Point>({ x: 0, y: 0 });
    readonly zoomLevel = signal(1);
    readonly fontReady = signal(false);

    readonly arrowSize = 8;
    constructor(
        public readonly ctx: CanvasRenderingContext2D,
        private readonly gridSize: number
    ) {
        void this.ensureFontLoaded();
    }

    private async ensureFontLoaded(): Promise<void> {
        try {
            const fonts = (document as unknown as { fonts?: FontFaceSet }).fonts;
            if (!fonts) {
                this.fontReady.set(true);
                return;
            }

            await fonts.load('600 14px Datatype');
            await fonts.ready;
            this.fontReady.set(true);
        } catch {
            this.fontReady.set(true);
        }
    }

    private roundedRect(x: number, y: number, width: number, height: number, radius: number): void {
        const maxRadius = Math.min(radius, width / 2, height / 2);
        this.ctx.beginPath();
        this.ctx.moveTo(x + maxRadius, y);
        this.ctx.lineTo(x + width - maxRadius, y);
        this.ctx.quadraticCurveTo(x + width, y, x + width, y + maxRadius);
        this.ctx.lineTo(x + width, y + height - maxRadius);
        this.ctx.quadraticCurveTo(x + width, y + height, x + width - maxRadius, y + height);
        this.ctx.lineTo(x + maxRadius, y + height);
        this.ctx.quadraticCurveTo(x, y + height, x, y + height - maxRadius);
        this.ctx.lineTo(x, y + maxRadius);
        this.ctx.quadraticCurveTo(x, y, x + maxRadius, y);
        this.ctx.closePath();
    }

    private setFont(weight: 500 | 600 | 700, size: number): void {
        this.ctx.font = this.fontReady()
            ? `${weight} ${size}px Datatype, system-ui, -apple-system, Segoe UI, Roboto, Arial, sans-serif`
            : `${weight} ${size}px system-ui, -apple-system, Segoe UI, Roboto, Arial, sans-serif`;
    }

    clear(width: number, height: number): void {
        this.ctx.clearRect(0, 0, width, height);
        this.ctx.strokeStyle = CanvasWrapper.theme.connection.stroke;
        this.ctx.fillStyle = CanvasWrapper.theme.connection.stroke;
        this.ctx.lineWidth = 2;
        this.setFont(600, 14);
        this.ctx.textBaseline = 'alphabetic';
    }

    drawConnections(connections: ConnectionModel[], nodes: NodeModel[]): void {
        for (const conn of connections) {
            const obstacles = nodes.filter((n) => n.id !== conn.from.id && n.id !== conn.to.id);
            conn.draw(this, obstacles);
        }
    }

    drawNodes(
        nodes: NodeModel[],
        connections: ConnectionModel[],
        drag: {
            from: number;
            anchor: number;
            x: number;
            y: number;
            side?: Side;
        } | null,
        hovered: number | null,
        hoveredAnchor: number | null,
        options?: { readOnly?: boolean },
    ) {
        const readOnly = options?.readOnly ?? false;

        for (const node of nodes) {
            const inputAnchorIndex = node.inputAnchorIndex();
            const connectedAnchorIndexes = new Set<number>(
                connections
                    .filter((connection) => connection.from.id === node.id)
                    .map((connection) => connection.anchor),
            );
            const hasIncomingConnection =
                inputAnchorIndex !== null &&
                connections.some((connection) => connection.to.id === node.id);
            const statusTheme = node.renderStatus ? CanvasWrapper.theme.status[node.renderStatus] : null;

            const grad = this.ctx.createLinearGradient(node.x, node.y, node.x + node.width, node.y + node.height);
            grad.addColorStop(0, CanvasWrapper.theme.surface);
            grad.addColorStop(1, CanvasWrapper.theme.surfaceAlt);

            this.ctx.save();
            this.ctx.shadowColor = CanvasWrapper.theme.nodeShadow;
            this.ctx.shadowBlur = 22;
            this.ctx.shadowOffsetY = 12;
            this.ctx.fillStyle = grad;
            this.ctx.strokeStyle = statusTheme?.border ?? CanvasWrapper.theme.nodeStroke;
            this.ctx.lineJoin = 'round';
            this.ctx.lineWidth = 1.4;

            const isHoveredDest =
                drag && node.id !== drag.from && hovered === node.id;
            const eligible =
                drag &&
                node.id !== drag.from &&
                !connections.some(
                    (c) =>
                        (c.from.id === drag.from && c.to.id === node.id) ||
                        (c.to.id === drag.from && c.from.id === node.id),
                );

            if (isHoveredDest) {
                this.ctx.strokeStyle = CanvasWrapper.theme.nodeStrokeHover;
                this.ctx.setLineDash([5, 4]);
            } else if (eligible) {
                this.ctx.strokeStyle = CanvasWrapper.theme.eligibleDash;
                this.ctx.globalAlpha = 0.55;
            }

            this.roundedRect(node.x, node.y, node.width, node.height, 18);
            this.ctx.fill();
            this.ctx.shadowColor = 'transparent';
            this.ctx.shadowBlur = 0;
            this.ctx.stroke();

            this.ctx.save();
            this.ctx.fillStyle = statusTheme?.accent ?? CanvasWrapper.theme.accent;
            this.roundedRect(node.x, node.y, 10, node.height, 18);
            this.ctx.fill();
            this.ctx.restore();

            this.ctx.fillStyle = CanvasWrapper.theme.surfaceAccent;
            this.roundedRect(node.x + 22, node.y + 18, 32, 32, 8);
            this.ctx.fill();

            this.ctx.fillStyle = statusTheme?.accent ?? CanvasWrapper.theme.accent;
            this.setFont(700, 12);
            this.ctx.fillText('::', node.x + 31, node.y + 39);

            this.ctx.fillStyle = CanvasWrapper.theme.nodeText;
            this.setFont(700, 16);
            this.drawWrappedText(
                node.name,
                node.x + 68,
                node.y + 34,
                node.width - (node.hasUpgradeWarning ? 126 : 90),
            );

            if (node.hasUpgradeWarning) {
                this.drawNodeWarning(node.x + node.width - 28, node.y + 30);
            }

            if (statusTheme && node.renderStatus) {
                this.drawStatusBadge(node, this.statusLabel(node.renderStatus), statusTheme.badgeFill, statusTheme.text);
            }

            this.ctx.fillStyle = CanvasWrapper.theme.nodeTextMuted;
            this.setFont(500, 12);
            this.ctx.fillText('Route task output between workflow steps', node.x + 24, node.y + 62);

            this.ctx.fillStyle = CanvasWrapper.theme.nodeSectionText;
            this.setFont(700, 11);
            this.ctx.fillText('SUCCESS OUTPUTS', node.x + 24, node.y + 110);

            this.setFont(600, 13);
            node.outputAnchors().forEach((anchor) => {
                this.ctx.fillStyle = CanvasWrapper.theme.nodeText;
                this.ctx.fillText(anchor.label, node.x + 28, anchor.y + 4);
            });

            if (node.outputAnchors().length === 0) {
                this.ctx.fillStyle = 'rgba(180, 191, 213, 0.92)';
                this.ctx.fillText('No success outputs', node.x + 28, node.y + 144);
            }

            this.ctx.beginPath();
            this.ctx.strokeStyle = 'rgba(188, 200, 224, 0.6)';
            this.ctx.lineWidth = 1;
            this.ctx.moveTo(node.x + 24, node.y + node.height - 44);
            this.ctx.lineTo(node.x + node.width - 24, node.y + node.height - 44);
            this.ctx.stroke();

            this.ctx.fillStyle = CanvasWrapper.theme.nodeDanger;
            this.setFont(700, 11);
            this.ctx.fillText('FAULT HANDLING', node.x + 24, node.y + node.height - 18);

            this.ctx.restore();

            const anchors = node.anchorDefinitions();
            this.ctx.save();
            if (readOnly) {
                this.ctx.restore();
                continue;
            }

            if (drag && drag.from === node.id) {
                const pos = anchors.find((anchor) => anchor.index === drag.anchor);
                if (pos) {
                    this.drawAnchorPort(pos.x, pos.y, node.anchorRadius(drag.anchor), true, true);
                }
            } else {
                for (const anchor of anchors) {
                    const isHover = hoveredAnchor === anchor.index;
                    const isConnected = anchor.kind === 'input'
                        ? hasIncomingConnection
                        : connectedAnchorIndexes.has(anchor.index);
                    const shouldShow = isConnected || (hovered === node.id);
                    if (!shouldShow) {
                        continue;
                    }

                    this.drawAnchorPort(
                        anchor.x,
                        anchor.y,
                        node.anchorRadius(anchor.index),
                        isHover,
                        isConnected,
                    );
                }

                const hoveredAnchorDef = hovered === node.id && hoveredAnchor !== null
                    ? node.anchorDefinition(hoveredAnchor)
                    : null;
                if (hoveredAnchorDef?.kind === 'input') {
                    this.drawTooltip(hoveredAnchorDef.x + 14, hoveredAnchorDef.y - 12, hoveredAnchorDef.label);
                }
            }
            this.ctx.restore();
        }
    }

    private drawAnchorPort(x: number, y: number, radius: number, isHover: boolean, isConnected: boolean): void {
        const ringColor = isHover || isConnected
            ? CanvasWrapper.theme.anchor.connected
            : CanvasWrapper.theme.anchor.normal;
        const ringWidth = isConnected ? 2.5 : 2;

        this.ctx.save();
        this.ctx.beginPath();
        this.ctx.fillStyle = CanvasWrapper.theme.anchor.fill;
        this.ctx.arc(x, y, radius, 0, 2 * Math.PI);
        this.ctx.fill();

        this.ctx.beginPath();
        this.ctx.lineWidth = ringWidth;
        this.ctx.strokeStyle = ringColor;
        this.ctx.arc(x, y, radius, 0, 2 * Math.PI);
        this.ctx.stroke();

        if (isConnected) {
            this.ctx.beginPath();
            this.ctx.fillStyle = ringColor;
            this.ctx.arc(x, y, Math.max(2.25, radius - 4.5), 0, 2 * Math.PI);
            this.ctx.fill();
        } else if (isHover) {
            this.ctx.beginPath();
            this.ctx.fillStyle = 'rgba(31, 73, 255, 0.16)';
            this.ctx.arc(x, y, Math.max(radius - 3, 2), 0, 2 * Math.PI);
            this.ctx.fill();
        }

        this.ctx.restore();
    }

    private drawTooltip(x: number, y: number, text: string): void {
        this.ctx.save();
        this.setFont(600, 11);
        const paddingX = 10;
        const width = this.ctx.measureText(text).width + paddingX * 2;
        const height = 24;

        this.ctx.fillStyle = CanvasWrapper.theme.tooltipBg;
        this.roundedRect(x, y, width, height, 8);
        this.ctx.fill();

        this.ctx.fillStyle = CanvasWrapper.theme.tooltipText;
        this.ctx.fillText(text, x + paddingX, y + 16);
        this.ctx.restore();
    }

    private drawStatusBadge(node: NodeModel, label: string, fill: string, text: string): void {
        this.ctx.save();
        this.setFont(700, 11);
        const paddingX = 10;
        const width = this.ctx.measureText(label).width + paddingX * 2;
        const height = 24;
        const x = node.x + node.width - width - 18 - (node.hasUpgradeWarning ? 26 : 0);
        const y = node.y + 18;

        this.ctx.fillStyle = fill;
        this.roundedRect(x, y, width, height, 12);
        this.ctx.fill();

        this.ctx.fillStyle = text;
        this.ctx.fillText(label, x + paddingX, y + 16);
        this.ctx.restore();
    }

    drawFailureTooltip(node: NodeModel, message: string): void {
        this.ctx.save();
        const width = 280;
        const paddingX = 14;
        const paddingY = 12;
        const title = 'Failure';
        const lines = this.wrapText(message, width - paddingX * 2);
        const bodyHeight = Math.max(lines.length, 1) * 16;
        const height = paddingY * 2 + 18 + bodyHeight + 8;
        const x = node.x + node.width / 2 - width / 2;
        const y = node.y - height - 16;

        this.ctx.fillStyle = CanvasWrapper.theme.tooltipBg;
        this.roundedRect(x, y, width, height, 12);
        this.ctx.fill();

        this.ctx.fillStyle = CanvasWrapper.theme.tooltipDanger;
        this.setFont(700, 11);
        this.ctx.fillText(title, x + paddingX, y + 18);

        this.ctx.fillStyle = CanvasWrapper.theme.tooltipText;
        this.setFont(500, 12);
        lines.forEach((line, index) => {
            this.ctx.fillText(line, x + paddingX, y + paddingY + 26 + index * 16);
        });

        this.ctx.restore();
    }

    private drawNodeWarning(x: number, y: number): void {
        this.ctx.save();
        this.ctx.beginPath();
        this.ctx.fillStyle = CanvasWrapper.theme.nodeWarning;
        this.ctx.arc(x, y, 12, 0, Math.PI * 2);
        this.ctx.fill();

        this.ctx.fillStyle = CanvasWrapper.theme.nodeWarningText;
        this.setFont(700, 12);
        this.ctx.textAlign = 'center';
        this.ctx.textBaseline = 'middle';
        this.ctx.fillText('!', x, y + 0.5);
        this.ctx.restore();
    }

    private statusLabel(status: NonNullable<NodeModel['renderStatus']>): string {
        switch (status) {
            case 'Completed':
                return 'SUCCEEDED';
            case 'InProgress':
                return 'IN PROGRESS';
            case 'Failed':
                return 'FAILED';
            case 'Pending':
                return 'PENDING';
            default:
                return 'NOT EXECUTED';
        }
    }

    snap(value: number): number {
        return Math.round(value / this.gridSize) * this.gridSize;
    }

    drawWrappedText(text: string, x: number, y: number, maxWidth: number): void {
        const lines = this.wrapText(text, maxWidth);
        for (const line of lines) {
            this.ctx.fillText(line, x, y);
            y += 18;
        }
    }

    private wrapText(text: string, maxWidth: number): string[] {
        const words = text.split(/\s+/);
        let line = '';
        const lines: string[] = [];
        for (const word of words) {
            const test = line ? `${line} ${word}` : word;
            if (this.ctx.measureText(test).width > maxWidth && line) {
                lines.push(line);
                line = word;
            } else {
                line = test;
            }
        }
        if (line) {
            lines.push(line);
        }
        return lines;
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
