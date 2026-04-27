import { NodeChannelSectionLayout, NodeModel, NodePortDefinition, Point, Side } from './node-model';
import { PathfinderObstacle, PathfinderOptions, Segment, orthogonalRoute, subtractRouteProblemSegments } from './pathfinder';
import { CanvasDetailTier, CanvasPortConnectivityIndex, CanvasValidationSeverity, RenderedConnection, RenderedNode } from './canvas-render-model';
import { signal } from '@angular/core';
import {
    CanvasBackgroundEffect,
    canvasPointToWorldPoint,
    getHighlightFalloff,
    getVisibleWorldBounds,
    getWorldRadiusFromScreenRadius,
} from './canvas-backdrop';

export class CanvasWrapper {
    private static readonly primaryFontFamilyName = '"Merriweather Sans"';
    private static readonly fallbackFontFamily = "'Merriweather Sans', 'Rosario', 'Segoe UI', Arial, sans-serif";

    private static readonly theme = {
        surface: '#ffffff',
        surfaceAlt: '#f7f9ff',
        surfaceAccent: '#eef3ff',
        sectionSurface: '#eef4ff',
        sectionSurfaceAlt: '#f8fbff',
        sectionBorder: 'rgba(168, 184, 227, 0.42)',
        nodeStroke: 'rgba(85, 108, 176, 0.16)',
        nodeStrokeHover: '#2d4bdd',
        nodeText: '#101728',
        nodeTextMuted: 'rgba(113, 131, 166, 0.88)',
        nodeSectionText: 'rgba(136, 153, 187, 0.95)',
        nodePropertyType: '#5970a6',
        nodeDanger: '#df3d3d',
        nodeWarning: '#f28c18',
        nodeWarningText: '#fff7ed',
        nodeShadow: 'rgba(57, 81, 153, 0.16)',
        accent: '#2d4bdd',
        tooltipBg: '#101728',
        tooltipText: '#ffffff',
        tooltipDanger: '#ffb8b8',
        anchor: {
            normal: '#cfdafe',
            hover: '#2d4bdd',
            stroke: 'rgba(56, 76, 140, 0.28)',
            fill: '#ffffff',
            connected: '#1f49ff',
            dockFill: '#eef4ff',
            dockInactiveFill: 'rgba(248, 251, 255, 0.92)',
            dockContainer: '#dce7ff',
            dockStem: 'rgba(93, 113, 166, 0.26)',
            dockSlot: 'rgba(49, 70, 126, 0.46)',
        },
        eligibleDash: 'rgba(45, 75, 221, 0.45)',
        connection: {
            stroke: '#2d4bdd',
            soft: 'rgba(45, 75, 221, 0.72)',
            selected: '#2563eb',
        },
        validation: {
            error: {
                stroke: '#d92d20',
                fill: 'rgba(255, 232, 229, 0.88)',
                surface: 'rgba(255, 232, 229, 0.74)',
                glow: 'rgba(217, 45, 32, 0.28)',
            },
            warning: {
                stroke: '#f59e0b',
                fill: 'rgba(255, 247, 220, 0.92)',
                surface: 'rgba(255, 247, 220, 0.78)',
                glow: 'rgba(245, 158, 11, 0.30)',
            },
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
        backdrop: {
            dotSpacing: 24,
            dotRadius: 1,
            dotOffset: 12,
            baseDotColor: 'rgba(118, 138, 176, 0.26)',
            hoverRadiusPx: 120,
            hoverDotRadiusBoost: 0.55,
            hoverColorRgb: '69, 108, 230',
            hoverMaxAlpha: 0.72,
        },
    } as const;

    readonly viewportOffset = signal<Point>({ x: 0, y: 0 });
    readonly zoomLevel = signal(1);
    readonly fontReady = signal(false);

    private stitchPattern: CanvasPattern | null = null;
    private readonly fontFamily: string;
    constructor(
        public readonly ctx: CanvasRenderingContext2D,
        private readonly gridSize: number
    ) {
        this.fontFamily = this.resolveFontFamily();
        void this.ensureFontLoaded();
    }

    private async ensureFontLoaded(): Promise<void> {
        try {
            const fonts = (document as unknown as { fonts?: FontFaceSet }).fonts;
            if (!fonts) {
                this.fontReady.set(true);
                return;
            }

            await fonts.load(`600 14px ${CanvasWrapper.primaryFontFamilyName}`);
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
            ? `${weight} ${size}px ${this.fontFamily}`
            : `${weight} ${size}px ${CanvasWrapper.fallbackFontFamily}`;
    }

    private resolveFontFamily(): string {
        if (typeof document === 'undefined') {
            return CanvasWrapper.fallbackFontFamily;
        }

        const configuredFontFamily = getComputedStyle(document.documentElement)
            .getPropertyValue('--app-font-family')
            .trim();

        return configuredFontFamily || CanvasWrapper.fallbackFontFamily;
    }

    clear(width: number, height: number): void {
        this.ctx.clearRect(0, 0, width, height);
        this.ctx.strokeStyle = CanvasWrapper.theme.connection.stroke;
        this.ctx.fillStyle = CanvasWrapper.theme.connection.stroke;
        this.ctx.lineWidth = 2;
        this.setFont(600, 14);
        this.ctx.textBaseline = 'alphabetic';
    }

    drawBackdrop(
        canvasWidth: number,
        canvasHeight: number,
        effect: CanvasBackgroundEffect,
        hoveredCanvasPoint: Point | null,
    ): void {
        if (effect !== 'stitch') {
            return;
        }

        const visibleBounds = getVisibleWorldBounds(
            canvasWidth,
            canvasHeight,
            this.viewportOffset(),
            this.zoomLevel(),
        );
        const pattern = this.getStitchPattern();
        if (pattern) {
            this.ctx.save();
            this.ctx.fillStyle = pattern;
            this.ctx.fillRect(
                visibleBounds.left,
                visibleBounds.top,
                visibleBounds.right - visibleBounds.left,
                visibleBounds.bottom - visibleBounds.top,
            );
            this.ctx.restore();
        }

        if (!hoveredCanvasPoint) {
            return;
        }

        const hoverCenter = canvasPointToWorldPoint(
            hoveredCanvasPoint,
            this.viewportOffset(),
            this.zoomLevel(),
        );
        const radiusWorld = getWorldRadiusFromScreenRadius(
            CanvasWrapper.theme.backdrop.hoverRadiusPx,
            this.zoomLevel(),
        );
        const radiusSquared = radiusWorld * radiusWorld;
        const startX = this.snapBackdropCoordinate(hoverCenter.x - radiusWorld);
        const endX = this.snapBackdropCoordinate(hoverCenter.x + radiusWorld);
        const startY = this.snapBackdropCoordinate(hoverCenter.y - radiusWorld);
        const endY = this.snapBackdropCoordinate(hoverCenter.y + radiusWorld);

        this.ctx.save();
        for (let x = startX; x <= endX; x += CanvasWrapper.theme.backdrop.dotSpacing) {
            for (let y = startY; y <= endY; y += CanvasWrapper.theme.backdrop.dotSpacing) {
                const deltaX = x - hoverCenter.x;
                const deltaY = y - hoverCenter.y;
                const distanceSquared = deltaX * deltaX + deltaY * deltaY;
                const strength = getHighlightFalloff(distanceSquared, radiusSquared);
                if (strength <= 0) {
                    continue;
                }

                this.ctx.globalAlpha = CanvasWrapper.theme.backdrop.hoverMaxAlpha * strength;
                this.ctx.fillStyle = `rgb(${CanvasWrapper.theme.backdrop.hoverColorRgb})`;
                this.ctx.beginPath();
                this.ctx.arc(
                    x,
                    y,
                    CanvasWrapper.theme.backdrop.dotRadius + CanvasWrapper.theme.backdrop.hoverDotRadiusBoost * strength,
                    0,
                    Math.PI * 2,
                );
                this.ctx.fill();
            }
        }
        this.ctx.restore();
    }

    drawConnections(
        connections: RenderedConnection[],
        selectedConnectionIds: ReadonlySet<string> = new Set<string>(),
        options?: {
            readOnly?: boolean;
            detailTier?: CanvasDetailTier;
            hoveredConnectionId?: string | null;
            hoveredConnectionRouteIndex?: number | null;
            channelFocusedConnectionIds?: ReadonlySet<string>;
            validationConnectionFocus?: {
                connectionId: string;
                routeIndex?: number | null;
                severity: CanvasValidationSeverity;
            } | null;
        },
    ): void {
        const hoveredConnectionId = options?.hoveredConnectionId ?? null;
        const hoveredConnectionRouteIndex = options?.hoveredConnectionRouteIndex ?? null;
        const channelFocusedConnectionIds = options?.channelFocusedConnectionIds ?? new Set<string>();
        const validationConnectionFocus = options?.validationConnectionFocus ?? null;
        const validationConnectionId = validationConnectionFocus?.connectionId ?? null;
        const hasConnectionFocus = hoveredConnectionId !== null ||
            channelFocusedConnectionIds.size > 0 ||
            validationConnectionId !== null;
        const focusedConnections: RenderedConnection[] = [];

        for (const conn of connections) {
            const selected = selectedConnectionIds.has(conn.id);
            const hovered = hoveredConnectionId === conn.id ||
                channelFocusedConnectionIds.has(conn.id) ||
                validationConnectionId === conn.id;
            if (selected || hovered) {
                focusedConnections.push(conn);
                continue;
            }

            conn.draw(this, {
                selected: false,
                hovered: false,
                dimmed: hasConnectionFocus,
            });
        }

        for (const conn of focusedConnections) {
            const directlyHovered = hoveredConnectionId === conn.id;
            const validationFocused = validationConnectionFocus?.connectionId === conn.id;
            conn.draw(this, {
                selected: selectedConnectionIds.has(conn.id),
                hovered: directlyHovered || channelFocusedConnectionIds.has(conn.id),
                hoveredRouteIndex: directlyHovered
                    ? hoveredConnectionRouteIndex
                    : validationFocused
                        ? validationConnectionFocus.routeIndex ?? null
                        : null,
                validationSeverity: validationFocused ? validationConnectionFocus.severity : null,
                dimmed: false,
            });
        }
    }

    drawNodes(
        nodes: RenderedNode[],
        portConnectivity: CanvasPortConnectivityIndex,
        drag: {
            from: number;
            anchor: number;
            x: number;
            y: number;
            side?: Side;
            portKey?: string | null;
        } | null,
        hovered: number | null,
        hoveredPortKey: string | null,
        selectedNodeIds: ReadonlySet<number> = new Set<number>(),
        options?: {
            readOnly?: boolean;
            detailTier?: CanvasDetailTier;
            blockedTargetNodeIds?: ReadonlySet<number>;
            focusedPortKeys?: ReadonlySet<string>;
            validationFocus?: {
                severity: CanvasValidationSeverity;
                nodeId: number | null;
                sectionKey?: string | null;
                portKeys?: ReadonlySet<string>;
            } | null;
        },
    ) {
        const readOnly = options?.readOnly ?? false;
        const detailTier = options?.detailTier ?? 'full';
        const blockedTargetNodeIds = options?.blockedTargetNodeIds ?? new Set<number>();
        const focusedPortKeys = options?.focusedPortKeys ?? new Set<string>();
        const validationFocus = options?.validationFocus ?? null;
        const validationPortKeys = validationFocus?.portKeys ?? new Set<string>();
        const connectedSummaryPortKeys = portConnectivity.summaryPortKeys;
        const connectedPropertyPortKeys = portConnectivity.propertyPortKeys;

        for (const node of nodes) {
            const layout = node.layout;

            const statusTheme = node.renderStatus ? CanvasWrapper.theme.status[node.renderStatus] : null;
            const isSelected = selectedNodeIds.has(node.id);
            const validationNodeFocused = validationFocus?.nodeId === node.id;
            const validationTheme = validationNodeFocused && validationFocus
                ? CanvasWrapper.theme.validation[validationFocus.severity]
                : null;

            this.ctx.save();
            this.ctx.shadowColor = detailTier === 'full' ? CanvasWrapper.theme.nodeShadow : 'transparent';
            this.ctx.shadowBlur = detailTier === 'full' ? 22 : 0;
            this.ctx.shadowOffsetY = detailTier === 'full' ? 12 : 0;
            this.ctx.fillStyle = detailTier === 'full'
                ? this.createNodeGradient(node)
                : CanvasWrapper.theme.surface;
            this.ctx.strokeStyle = validationTheme
                ? validationTheme.stroke
                : isSelected
                ? CanvasWrapper.theme.connection.selected
                : statusTheme?.border ?? CanvasWrapper.theme.nodeStroke;
            this.ctx.lineJoin = 'round';
            this.ctx.lineWidth = validationTheme ? 3.2 : isSelected ? 3 : 1.4;

            const isHoveredDest =
                drag && node.id !== drag.from && hovered === node.id;
            const eligible =
                drag &&
                node.id !== drag.from &&
                !blockedTargetNodeIds.has(node.id);

            if (isHoveredDest) {
                this.ctx.strokeStyle = CanvasWrapper.theme.nodeStrokeHover;
                this.ctx.setLineDash([5, 4]);
            } else if (eligible) {
                this.ctx.strokeStyle = CanvasWrapper.theme.eligibleDash;
                this.ctx.globalAlpha = 0.55;
            }

            if (validationTheme) {
                this.ctx.save();
                this.ctx.shadowColor = validationTheme.glow;
                this.ctx.shadowBlur = 26;
                this.ctx.lineWidth = 8;
                this.ctx.strokeStyle = validationTheme.glow;
                this.roundedRect(node.x - 3, node.y - 3, node.width + 6, node.height + 6, 22);
                this.ctx.stroke();
                this.ctx.restore();
            }

            this.roundedRect(node.x, node.y, node.width, node.height, 18);
            this.ctx.fill();
            this.ctx.shadowColor = 'transparent';
            this.ctx.shadowBlur = 0;
            this.ctx.stroke();

            this.ctx.fillStyle = CanvasWrapper.theme.surfaceAccent;
            this.roundedRect(node.x + 18, node.y + 14, 34, 34, 10);
            this.ctx.fill();

            this.ctx.fillStyle = statusTheme?.accent ?? CanvasWrapper.theme.accent;
            this.setFont(700, 12);
            this.ctx.fillText('::', node.x + 28, node.y + 35);

            this.ctx.fillStyle = CanvasWrapper.theme.nodeText;
            this.setFont(700, 16);
            this.drawEllipsizedText(
                node.name,
                node.x + 66,
                layout.titleBaseline,
                layout.titleMaxWidth - (node.hasUpgradeWarning ? 30 : 0),
            );

            if (node.hasUpgradeWarning) {
                this.drawNodeWarning(node.x + node.width - 78, node.y + 30);
            }

            this.ctx.fillStyle = CanvasWrapper.theme.nodeTextMuted;
            this.setFont(500, 12);
            this.drawEllipsizedText(
                node.resolvedVersion ?? '0.0.1',
                node.x + 66,
                layout.versionBaseline,
                layout.titleMaxWidth,
            );

            if (readOnly && statusTheme && node.renderStatus) {
                this.drawNodeStatusIcon(node, node.renderStatus, statusTheme.badgeFill, statusTheme.text, statusTheme.accent);
            }

            if (!readOnly && node.hasDesignTimeOverrides) {
                this.drawNodeOverrideBadge(node, `${node.designTimeOverrideCount} override${node.designTimeOverrideCount === 1 ? '' : 's'}`);
            }

            if (!readOnly) {
                this.drawNodeSettingsButton(node);
            }
            if (layout.inputSection) {
                this.drawChannelSection(node, layout.inputSection, hovered === node.id, hoveredPortKey, connectedSummaryPortKeys, connectedPropertyPortKeys, drag?.from === node.id ? drag.portKey ?? null : null, focusedPortKeys, validationNodeFocused ? validationFocus : null, validationPortKeys);
            }
            layout.outputSections.forEach((section) => {
                this.drawChannelSection(node, section, hovered === node.id, hoveredPortKey, connectedSummaryPortKeys, connectedPropertyPortKeys, drag?.from === node.id ? drag.portKey ?? null : null, focusedPortKeys, validationNodeFocused ? validationFocus : null, validationPortKeys);
            });

            this.ctx.restore();
        }
    }

    private drawChannelSection(
        node: RenderedNode,
        section: NodeChannelSectionLayout,
        isHoveredNode: boolean,
        hoveredPortKey: string | null,
        connectedSummaryPortKeys: ReadonlySet<string>,
        connectedPropertyPortKeys: ReadonlySet<string>,
        draggingPortKey: string | null,
        focusedPortKeys: ReadonlySet<string>,
        validationFocus: {
            severity: CanvasValidationSeverity;
            nodeId: number | null;
            sectionKey?: string | null;
            portKeys?: ReadonlySet<string>;
        } | null,
        validationPortKeys: ReadonlySet<string>,
    ): void {
        this.ctx.save();

        const summaryPort = section.summaryPort;
        const summaryScopedKey = this.portFocusKey(node, summaryPort);
        const validationTheme = validationFocus ? CanvasWrapper.theme.validation[validationFocus.severity] : null;
        const validationSectionFocused = validationFocus?.sectionKey === section.key;
        const summaryValidationFocused = validationSectionFocused || validationPortKeys.has(summaryScopedKey);
        const summaryFocused = focusedPortKeys.has(summaryScopedKey) || summaryValidationFocused;
        const summaryHover = (isHoveredNode && hoveredPortKey === summaryPort.key) || summaryFocused;
        const summaryConnected = connectedSummaryPortKeys.has(summaryScopedKey);
        const summaryActive = summaryHover || draggingPortKey === summaryPort.key;

        this.ctx.fillStyle = summaryValidationFocused && validationTheme
            ? validationTheme.surface
            : summaryActive
            ? 'rgba(45, 75, 221, 0.13)'
            : CanvasWrapper.theme.sectionSurface;
        this.roundedRect(node.x + 14, section.headerTop, node.width - 28, section.headerHeight, 12);
        this.ctx.fill();

        this.ctx.strokeStyle = summaryValidationFocused && validationTheme
            ? validationTheme.stroke
            : summaryActive
            ? 'rgba(45, 75, 221, 0.34)'
            : CanvasWrapper.theme.sectionBorder;
        this.ctx.lineWidth = summaryValidationFocused ? 2 : summaryActive ? 1.35 : 1;
        this.roundedRect(node.x + 14, section.headerTop, node.width - 28, section.headerHeight, 12);
        this.ctx.stroke();

        this.ctx.fillStyle = CanvasWrapper.theme.nodeSectionText;
        this.setFont(700, 11);

        this.ctx.fillStyle = CanvasWrapper.theme.accent;
        this.setFont(700, 10);
        const countLabel = section.countLabel.toUpperCase();
        const countWidth = this.ctx.measureText(countLabel).width;
        const countX = section.toggleRect.x - 8 - countWidth;
        this.ctx.fillText(countLabel, countX, section.headerTop + 20);

        this.ctx.fillStyle = CanvasWrapper.theme.nodeSectionText;
        this.setFont(700, 11);
        const sectionLabelX = node.x + 34;
        this.drawEllipsizedText(
            section.label.toUpperCase(),
            sectionLabelX,
            section.headerTop + 20,
            Math.max(48, countX - sectionLabelX - 12),
        );
        this.drawSectionToggle(section);

        if (section.propertyRows.length > 0) {
            for (const row of section.propertyRows) {
                this.ctx.fillStyle = row === section.propertyRows[0]
                    ? CanvasWrapper.theme.surface
                    : CanvasWrapper.theme.sectionSurfaceAlt;
                this.fillRect(node.x + 18, row.top, node.width - 36, row.height);

                const rowScopedKey = this.portFocusKey(node, row.port);
                const rowValidationFocused = validationPortKeys.has(rowScopedKey);
                if (rowValidationFocused && validationTheme) {
                    this.ctx.fillStyle = validationTheme.fill;
                    this.roundedRect(node.x + 20, row.top + 3, node.width - 40, row.height - 6, 10);
                    this.ctx.fill();
                }

                this.ctx.strokeStyle = 'rgba(188, 200, 224, 0.56)';
                this.ctx.lineWidth = 1;
                this.ctx.beginPath();
                this.ctx.moveTo(node.x + 22, row.top);
                this.ctx.lineTo(node.x + node.width - 22, row.top);
                this.ctx.stroke();

                const propertyType = row.kind.toUpperCase();
                this.setFont(600, 10);
                const propertyTypeWidth = this.ctx.measureText(propertyType).width;
                const propertyTypeX = node.x + node.width - 34 - propertyTypeWidth;
                const labelX = node.x + 34;
                const labelMaxWidth = Math.max(40, propertyTypeX - labelX - (row.required ? 18 : 8));

                this.ctx.fillStyle = CanvasWrapper.theme.nodeText;
                this.setFont(600, 13);
                const displayedLabel = this.drawEllipsizedText(row.label, labelX, row.centerY + 5, labelMaxWidth);

                if (row.required) {
                    this.ctx.fillStyle = CanvasWrapper.theme.accent;
                    this.setFont(700, 13);
                    const labelWidth = Math.min(this.ctx.measureText(displayedLabel).width, labelMaxWidth);
                    this.ctx.fillText('*', labelX + labelWidth + 4, row.centerY + 5);
                }

                this.ctx.fillStyle = CanvasWrapper.theme.nodePropertyType;
                this.setFont(600, 10);
                this.ctx.fillText(propertyType, propertyTypeX, row.centerY + 4);

                const isFocusedEndpoint = focusedPortKeys.has(rowScopedKey);
                const isHover = (isHoveredNode && hoveredPortKey === row.port.key) || isFocusedEndpoint || rowValidationFocused;
                const isConnected = connectedPropertyPortKeys.has(rowScopedKey);
                const shouldShow = section.expanded ||
                    isConnected ||
                    isHoveredNode ||
                    draggingPortKey === row.port.key ||
                    isFocusedEndpoint ||
                    rowValidationFocused;
                if (shouldShow) {
                    this.drawPropertySocket(
                        row.port,
                        isHover || draggingPortKey === row.port.key,
                        isConnected || draggingPortKey === row.port.key,
                        draggingPortKey === row.port.key,
                        rowValidationFocused ? validationFocus?.severity ?? null : null,
                    );
                }
            }
        }

        this.drawSummaryDock(
            node,
            section,
            summaryActive,
            summaryConnected || draggingPortKey === summaryPort.key,
            summaryValidationFocused ? validationFocus?.severity ?? null : null,
        );

        this.ctx.restore();
    }

    private drawSectionToggle(section: NodeChannelSectionLayout): void {
        const rect = section.toggleRect;
        const centerX = rect.x + rect.width / 2;
        const centerY = rect.y + rect.height / 2;

        this.ctx.save();
        this.ctx.fillStyle = section.expanded ? 'rgba(45, 75, 221, 0.10)' : 'rgba(16, 23, 40, 0.04)';
        this.roundedRect(rect.x, rect.y, rect.width, rect.height, 9);
        this.ctx.fill();

        this.ctx.strokeStyle = section.expanded ? CanvasWrapper.theme.accent : 'rgba(115, 132, 170, 0.55)';
        this.ctx.lineWidth = 1.5;
        this.ctx.beginPath();
        this.ctx.moveTo(centerX - 5, centerY);
        this.ctx.lineTo(centerX + 5, centerY);
        this.ctx.stroke();

        if (!section.expanded) {
            this.ctx.beginPath();
            this.ctx.moveTo(centerX, centerY - 5);
            this.ctx.lineTo(centerX, centerY + 5);
            this.ctx.stroke();
        }

        this.ctx.restore();
    }

    private portFocusKey(node: RenderedNode, port: NodePortDefinition): string {
        return `${node.id}:${port.key}`;
    }

    private createNodeGradient(node: RenderedNode): CanvasGradient {
        const gradient = this.ctx.createLinearGradient(node.x, node.y, node.x + node.width, node.y + node.height);
        gradient.addColorStop(0, CanvasWrapper.theme.surface);
        gradient.addColorStop(1, CanvasWrapper.theme.surfaceAlt);
        return gradient;
    }

    private drawNodeSettingsButton(node: RenderedNode): void {
        const settingsRect = node.settingsRect();
        const centerX = settingsRect.x + settingsRect.width / 2;
        const centerY = settingsRect.y + settingsRect.height / 2;

        this.ctx.save();
        this.ctx.fillStyle = 'rgba(16, 23, 40, 0.04)';
        this.roundedRect(settingsRect.x, settingsRect.y, settingsRect.width, settingsRect.height, 10);
        this.ctx.fill();

        this.ctx.strokeStyle = 'rgba(115, 132, 170, 0.62)';
        this.ctx.lineWidth = 1.6;
        this.ctx.lineCap = 'round';
        this.ctx.lineJoin = 'round';

        this.ctx.beginPath();
        for (let index = 0; index < 16; index += 1) {
            const angle = (Math.PI * 2 * index) / 16 - Math.PI / 2;
            const radius = index % 2 === 0 ? 8.8 : 6.7;
            const x = centerX + Math.cos(angle) * radius;
            const y = centerY + Math.sin(angle) * radius;
            if (index === 0) {
                this.ctx.moveTo(x, y);
            } else {
                this.ctx.lineTo(x, y);
            }
        }
        this.ctx.closePath();
        this.ctx.stroke();

        this.ctx.beginPath();
        this.ctx.arc(centerX, centerY, 3.2, 0, Math.PI * 2);
        this.ctx.stroke();

        this.ctx.restore();
    }

    private fillRect(x: number, y: number, width: number, height: number): void {
        this.ctx.beginPath();
        this.ctx.rect(x, y, width, height);
        this.ctx.fill();
    }

    private drawSummaryDock(
        node: RenderedNode,
        section: NodeChannelSectionLayout,
        isHover: boolean,
        isConnected: boolean,
        validationSeverity: CanvasValidationSeverity | null = null,
    ): void {
        const port = section.summaryPort;
        const validationTheme = validationSeverity ? CanvasWrapper.theme.validation[validationSeverity] : null;
        const bounds = port.visualBounds ?? {
            x: port.x - port.radius,
            y: port.y - port.radius,
            width: port.radius * 2,
            height: port.radius * 2,
        };
        const dockRadius = Math.min(9, bounds.width / 2, bounds.height / 2);
        const dockInnerX = section.side === 'left'
            ? bounds.x + bounds.width
            : bounds.x;
        const channelEdgeX = section.side === 'left'
            ? node.x + 14
            : node.x + node.width - 14;
        const slotX = bounds.x + bounds.width / 2;
        const outerX = section.side === 'left'
            ? bounds.x
            : bounds.x + bounds.width;
        const stateLayerRadius = dockRadius + 5;

        this.ctx.save();

        if (isHover) {
            this.ctx.fillStyle = validationTheme?.surface ?? 'rgba(31, 73, 255, 0.14)';
            this.roundedRect(bounds.x - 5, bounds.y - 5, bounds.width + 10, bounds.height + 10, stateLayerRadius);
            this.ctx.fill();
        }

        this.ctx.shadowColor = isHover || isConnected
            ? validationTheme?.glow ?? 'rgba(45, 75, 221, 0.22)'
            : 'rgba(57, 81, 153, 0.14)';
        this.ctx.shadowBlur = isHover || isConnected ? 12 : 7;
        this.ctx.shadowOffsetY = 2;

        this.ctx.strokeStyle = isHover || isConnected
            ? validationTheme?.stroke ?? 'rgba(31, 73, 255, 0.46)'
            : CanvasWrapper.theme.anchor.dockStem;
        this.ctx.lineWidth = isHover || isConnected ? 2 : 1.3;
        this.ctx.beginPath();
        this.ctx.moveTo(channelEdgeX, port.y);
        this.ctx.lineTo(dockInnerX, port.y);
        this.ctx.stroke();

        const dockGradient = this.ctx.createLinearGradient(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height);
        dockGradient.addColorStop(0, isConnected || isHover
            ? validationTheme?.fill ?? CanvasWrapper.theme.anchor.dockFill
            : CanvasWrapper.theme.anchor.dockInactiveFill);
        dockGradient.addColorStop(1, isConnected
            ? CanvasWrapper.theme.anchor.dockContainer
            : '#ffffff');
        this.ctx.fillStyle = dockGradient;
        this.roundedRect(bounds.x, bounds.y, bounds.width, bounds.height, dockRadius);
        this.ctx.fill();

        this.ctx.shadowColor = 'transparent';
        this.ctx.shadowBlur = 0;
        this.ctx.shadowOffsetY = 0;

        this.ctx.strokeStyle = isHover || isConnected
            ? validationTheme?.stroke ?? CanvasWrapper.theme.anchor.connected
            : CanvasWrapper.theme.anchor.normal;
        this.ctx.lineWidth = isConnected ? 2.3 : 1.6;
        this.roundedRect(bounds.x, bounds.y, bounds.width, bounds.height, dockRadius);
        this.ctx.stroke();

        const inset = 3;
        this.ctx.strokeStyle = isHover || isConnected
            ? validationTheme?.stroke ?? 'rgba(31, 73, 255, 0.28)'
            : 'rgba(82, 103, 154, 0.18)';
        this.ctx.lineWidth = 1;
        this.roundedRect(bounds.x + inset, bounds.y + inset, bounds.width - inset * 2, bounds.height - inset * 2, Math.max(2, dockRadius - inset));
        this.ctx.stroke();

        this.ctx.strokeStyle = isHover || isConnected
            ? validationTheme?.stroke ?? CanvasWrapper.theme.anchor.connected
            : CanvasWrapper.theme.anchor.dockSlot;
        this.ctx.lineWidth = isConnected ? 2.5 : 1.8;
        this.ctx.lineCap = 'round';
        this.ctx.beginPath();
        this.ctx.moveTo(outerX, port.y - 9);
        this.ctx.lineTo(outerX, port.y + 9);
        this.ctx.stroke();

        this.ctx.fillStyle = isConnected
            ? validationTheme?.stroke ?? CanvasWrapper.theme.anchor.connected
            : CanvasWrapper.theme.anchor.dockSlot;
        for (const yOffset of [-7, 0, 7]) {
            this.ctx.beginPath();
            this.ctx.arc(slotX, port.y + yOffset, isConnected ? 2.05 : 1.6, 0, 2 * Math.PI);
            this.ctx.fill();
        }

        this.ctx.restore();
    }

    private drawPropertySocket(
        port: NodePortDefinition,
        isHover: boolean,
        isConnected: boolean,
        isGrabbed: boolean,
        validationSeverity: CanvasValidationSeverity | null = null,
    ): void {
        const x = port.x;
        const y = port.y;
        const validationTheme = validationSeverity ? CanvasWrapper.theme.validation[validationSeverity] : null;
        const radius = port.radius;
        const biteRadius = radius + 2.5;
        const activeRadius = radius + 6;
        const isInput = port.side === 'left';
        const innerStart = isInput ? -Math.PI / 2 : Math.PI / 2;
        const innerEnd = isInput ? Math.PI / 2 : Math.PI * 1.5;

        this.ctx.save();

        // The "bite" is a concave half-circle cut into the node border.
        this.ctx.beginPath();
        this.ctx.fillStyle = CanvasWrapper.theme.surfaceAlt;
        this.ctx.moveTo(x, y + (isInput ? -biteRadius : biteRadius));
        this.ctx.arc(x, y, biteRadius, innerStart, innerEnd, false);
        this.ctx.closePath();
        this.ctx.fill();

        this.ctx.beginPath();
        this.ctx.lineWidth = isConnected ? 2 : 1.35;
        this.ctx.strokeStyle = isConnected
            ? validationTheme?.stroke ?? CanvasWrapper.theme.anchor.connected
            : 'rgba(88, 105, 152, 0.36)';
        this.ctx.arc(x, y, biteRadius, innerStart, innerEnd, false);
        this.ctx.stroke();

        if (isHover || isGrabbed) {
            this.ctx.beginPath();
            this.ctx.fillStyle = isGrabbed
                ? CanvasWrapper.theme.anchor.connected
                : validationTheme?.surface ?? 'rgba(31, 73, 255, 0.34)';
            this.ctx.arc(x, y, activeRadius, 0, 2 * Math.PI);
            this.ctx.fill();

            if (!isGrabbed) {
                this.ctx.beginPath();
                this.ctx.strokeStyle = validationTheme?.stroke ?? 'rgba(31, 73, 255, 0.72)';
                this.ctx.lineWidth = 1.4;
                this.ctx.arc(x, y, activeRadius, 0, 2 * Math.PI);
                this.ctx.stroke();
            }
        } else if (isConnected) {
            this.ctx.beginPath();
            this.ctx.fillStyle = validationTheme?.stroke ?? CanvasWrapper.theme.anchor.connected;
            this.ctx.arc(x, y, 2.35, 0, 2 * Math.PI);
            this.ctx.fill();
        }

        this.ctx.restore();
    }

    private drawNodeStatusIcon(
        node: RenderedNode,
        status: NonNullable<NodeModel['renderStatus']>,
        fill: string,
        text: string,
        accent: string,
    ): void {
        const rect = node.settingsRect();
        const centerX = rect.x + rect.width / 2;
        const centerY = rect.y + rect.height / 2;

        this.ctx.save();

        this.ctx.fillStyle = fill;
        this.roundedRect(rect.x, rect.y, rect.width, rect.height, 10);
        this.ctx.fill();

        this.ctx.strokeStyle = accent;
        this.ctx.globalAlpha = 0.34;
        this.ctx.lineWidth = 1.4;
        this.roundedRect(rect.x + 0.7, rect.y + 0.7, rect.width - 1.4, rect.height - 1.4, 9.5);
        this.ctx.stroke();

        this.ctx.globalAlpha = 1;
        this.ctx.strokeStyle = text;
        this.ctx.fillStyle = text;
        this.ctx.lineWidth = 2.2;
        this.ctx.lineCap = 'round';
        this.ctx.lineJoin = 'round';

        switch (status) {
            case 'Completed':
                this.ctx.beginPath();
                this.ctx.moveTo(centerX - 6, centerY + 0.5);
                this.ctx.lineTo(centerX - 2.2, centerY + 4.6);
                this.ctx.lineTo(centerX + 7, centerY - 5);
                this.ctx.stroke();
                break;
            case 'Failed':
                this.ctx.beginPath();
                this.ctx.moveTo(centerX - 5.5, centerY - 5.5);
                this.ctx.lineTo(centerX + 5.5, centerY + 5.5);
                this.ctx.moveTo(centerX + 5.5, centerY - 5.5);
                this.ctx.lineTo(centerX - 5.5, centerY + 5.5);
                this.ctx.stroke();
                break;
            case 'InProgress':
                this.ctx.beginPath();
                this.ctx.arc(centerX, centerY, 6.4, -Math.PI * 0.88, Math.PI * 0.62);
                this.ctx.stroke();
                this.ctx.beginPath();
                this.ctx.moveTo(centerX + 5.7, centerY - 4.8);
                this.ctx.lineTo(centerX + 8.2, centerY - 1.4);
                this.ctx.lineTo(centerX + 4.1, centerY - 1.1);
                this.ctx.fill();
                break;
            case 'Pending':
                this.ctx.beginPath();
                this.ctx.arc(centerX, centerY, 6.4, 0, Math.PI * 2);
                this.ctx.stroke();
                this.ctx.beginPath();
                this.ctx.moveTo(centerX, centerY - 3.8);
                this.ctx.lineTo(centerX, centerY + 0.6);
                this.ctx.lineTo(centerX + 3.4, centerY + 3.2);
                this.ctx.stroke();
                break;
            default:
                this.ctx.beginPath();
                this.ctx.arc(centerX, centerY, 3.1, 0, Math.PI * 2);
                this.ctx.fill();
                break;
        }

        this.ctx.restore();
    }

    private drawNodeOverrideBadge(node: RenderedNode, label: string): void {
        this.ctx.save();
        this.setFont(700, 11);
        const paddingX = 10;
        const iconSize = 16;
        const width = this.ctx.measureText(label).width + paddingX * 2 + iconSize + 8;
        const height = 24;
        const x = node.x + 24;
        const y = node.y + 74;

        this.ctx.fillStyle = 'rgba(242, 140, 24, 0.12)';
        this.roundedRect(x, y, width, height, 12);
        this.ctx.fill();

        this.ctx.fillStyle = '#c2410c';
        this.ctx.beginPath();
        this.ctx.arc(x + 12, y + height / 2, 5, 0, Math.PI * 2);
        this.ctx.fill();

        this.ctx.fillStyle = '#9a3412';
        this.ctx.fillText(label, x + 12 + iconSize, y + 16);
        this.ctx.restore();
    }

    drawFailureTooltip(node: RenderedNode, message: string): void {
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

    drawMarqueeRect(left: number, top: number, right: number, bottom: number): void {
        const width = right - left;
        const height = bottom - top;

        this.ctx.save();
        this.ctx.fillStyle = 'rgba(37, 99, 235, 0.12)';
        this.ctx.strokeStyle = 'rgba(37, 99, 235, 0.82)';
        this.ctx.lineWidth = 1.5;
        this.ctx.setLineDash([8, 6]);
        this.ctx.fillRect(left, top, width, height);
        this.ctx.strokeRect(left, top, width, height);
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

    drawEllipsizedText(text: string, x: number, y: number, maxWidth: number): string {
        const displayText = this.ellipsizeText(text, maxWidth);
        if (displayText.length > 0) {
            this.ctx.fillText(displayText, x, y);
        }

        return displayText;
    }

    private ellipsizeText(text: string, maxWidth: number): string {
        if (maxWidth <= 0 || text.length === 0) {
            return '';
        }

        if (this.ctx.measureText(text).width <= maxWidth) {
            return text;
        }

        const ellipsis = '...';
        if (this.ctx.measureText(ellipsis).width > maxWidth) {
            return '';
        }

        let low = 0;
        let high = text.length;
        let best = ellipsis;
        while (low <= high) {
            const mid = Math.floor((low + high) / 2);
            const candidate = `${text.slice(0, mid).trimEnd()}${ellipsis}`;
            if (this.ctx.measureText(candidate).width <= maxWidth) {
                best = candidate;
                low = mid + 1;
            } else {
                high = mid - 1;
            }
        }

        return best;
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

    drawOrthogonalLine(
        sx: number,
        sy: number,
        ex: number,
        ey: number,
        obstacles: PathfinderObstacle[] = [],
        side?: Side,
        destNode?: PathfinderObstacle,
        startSide?: Side,
        startNode?: PathfinderObstacle,
    ): void {
        const options: PathfinderOptions = {
            gridSize: this.gridSize,
            nodeMargin: NodeModel.buffer,
            obstacleBuffer: Math.max(NodeModel.buffer / this.gridSize, 1),
        };
        const route = orthogonalRoute(
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
        const segments = route.segments;
        if (segments.length === 0) {
            return;
        }

        if (route.status === 'obstacleViolation') {
            this.ctx.save();
            this.ctx.strokeStyle = '#d92d20';
        }

        if (route.problemSegments.length > 0) {
            for (const segment of subtractRouteProblemSegments(segments, route.problemSegments)) {
                this.ctx.beginPath();
                this.ctx.moveTo(segment.x1, segment.y1);
                this.ctx.lineTo(segment.x2, segment.y2);
                this.ctx.stroke();
            }

            this.ctx.setLineDash([8, 6]);
            this.ctx.lineWidth = Math.max(this.ctx.lineWidth, 3);
            for (const problem of route.problemSegments) {
                this.ctx.beginPath();
                this.ctx.moveTo(problem.x1, problem.y1);
                this.ctx.lineTo(problem.x2, problem.y2);
                this.ctx.stroke();
            }
            this.ctx.setLineDash([]);
        } else {
            this.drawRoundedOrthogonalSegments(segments);
        }

        if (route.status === 'obstacleViolation') {
            this.ctx.restore();
        }
    }

    pathfinderOptions(): PathfinderOptions {
        return {
            gridSize: this.gridSize,
            nodeMargin: NodeModel.buffer,
            obstacleBuffer: Math.max(NodeModel.buffer / this.gridSize, 1),
        };
    }

    private drawRoundedOrthogonalSegments(segments: readonly Segment[]): void {
        if (segments.length === 0) {
            return;
        }

        const points = [
            { x: segments[0]!.x1, y: segments[0]!.y1 },
            ...segments.map((segment) => ({ x: segment.x2, y: segment.y2 })),
        ];
        if (points.length < 3) {
            this.ctx.beginPath();
            this.ctx.moveTo(segments[0]!.x1, segments[0]!.y1);
            this.ctx.lineTo(segments[0]!.x2, segments[0]!.y2);
            this.ctx.stroke();
            return;
        }

        const cornerRadius = 7;
        this.ctx.beginPath();
        this.ctx.moveTo(points[0]!.x, points[0]!.y);

        for (let index = 1; index < points.length - 1; index += 1) {
            const previous = points[index - 1]!;
            const current = points[index]!;
            const next = points[index + 1]!;

            if (
                (previous.x === current.x && current.x === next.x) ||
                (previous.y === current.y && current.y === next.y)
            ) {
                this.ctx.lineTo(current.x, current.y);
                continue;
            }

            const previousDistance = Math.abs(current.x - previous.x) + Math.abs(current.y - previous.y);
            const nextDistance = Math.abs(next.x - current.x) + Math.abs(next.y - current.y);
            const radius = Math.min(cornerRadius, previousDistance / 2, nextDistance / 2);
            if (radius <= 0) {
                this.ctx.lineTo(current.x, current.y);
                continue;
            }

            const startRatio = radius / previousDistance;
            const endRatio = radius / nextDistance;
            const cornerStart = {
                x: current.x + (previous.x - current.x) * startRatio,
                y: current.y + (previous.y - current.y) * startRatio,
            };
            const cornerEnd = {
                x: current.x + (next.x - current.x) * endRatio,
                y: current.y + (next.y - current.y) * endRatio,
            };
            this.ctx.lineTo(cornerStart.x, cornerStart.y);
            this.ctx.quadraticCurveTo(current.x, current.y, cornerEnd.x, cornerEnd.y);
        }

        const lastPoint = points[points.length - 1]!;
        this.ctx.lineTo(lastPoint.x, lastPoint.y);
        this.ctx.stroke();
    }

    private getStitchPattern(): CanvasPattern | null {
        if (this.stitchPattern) {
            return this.stitchPattern;
        }

        if (typeof document === 'undefined') {
            return null;
        }

        const tile = document.createElement('canvas');
        const size = CanvasWrapper.theme.backdrop.dotSpacing;
        tile.width = size;
        tile.height = size;

        const tileContext = tile.getContext('2d');
        if (!tileContext) {
            return null;
        }

        tileContext.fillStyle = CanvasWrapper.theme.backdrop.baseDotColor;
        tileContext.beginPath();
        tileContext.arc(
            CanvasWrapper.theme.backdrop.dotOffset,
            CanvasWrapper.theme.backdrop.dotOffset,
            CanvasWrapper.theme.backdrop.dotRadius,
            0,
            Math.PI * 2,
        );
        tileContext.fill();

        this.stitchPattern = this.ctx.createPattern(tile, 'repeat');
        return this.stitchPattern;
    }

    private snapBackdropCoordinate(value: number): number {
        const { dotSpacing, dotOffset } = CanvasWrapper.theme.backdrop;
        return Math.floor((value - dotOffset) / dotSpacing) * dotSpacing + dotOffset;
    }
}
