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

export interface CanvasPropertyIssueSummary {
    readonly severity: CanvasValidationSeverity;
    readonly count: number;
}

export interface CanvasNodeIssueSummary {
    readonly severity: CanvasValidationSeverity;
    readonly errorCount: number;
    readonly warningCount: number;
    readonly tooltip: string;
    readonly propertyIssues: ReadonlyMap<string, CanvasPropertyIssueSummary>;
}

export class CanvasWrapper {
    private static readonly primaryFontFamilyName = '"Inter"';
    private static readonly materialIconFontFamilyName = '"Material Icons"';
    private static readonly materialIconFontFamily = "'Material Icons'";
    private static readonly fallbackFontFamily = "Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif";

    private static readonly theme = {
        surface: '#fbfcff',
        surfaceAlt: '#f6f8fc',
        surfaceAccent: '#eef3fb',
        sectionSurface: '#f6f8fc',
        sectionSurfaceActive: '#edf4ff',
        sectionSurfaceAlt: '#fbfcff',
        sectionRowSurface: '#fbfcff',
        sectionRowActive: '#eef4ff',
        sectionBorder: 'rgba(143, 158, 194, 0.28)',
        sectionBorderActive: 'rgba(45, 75, 221, 0.24)',
        sectionDivider: 'rgba(190, 201, 225, 0.38)',
        nodeStroke: 'rgba(75, 96, 148, 0.16)',
        nodeStrokeHover: '#2d4bdd',
        nodeText: '#101728',
        nodeTextMuted: 'rgba(113, 131, 166, 0.88)',
        nodeSectionText: 'rgba(103, 121, 158, 0.88)',
        nodePropertyType: '#6f7f9f',
        nodeDanger: '#df3d3d',
        nodeWarning: '#f28c18',
        nodeWarningText: '#fdf2e9',
        nodeShadow: 'rgba(46, 65, 116, 0.10)',
        accent: '#2d4bdd',
        tooltipBg: '#101728',
        tooltipText: '#f6f8ff',
        tooltipDanger: '#ffb8b8',
        anchor: {
            normal: '#b8c5d8',
            hover: '#2d4bdd',
            stroke: 'rgba(67, 84, 125, 0.28)',
            fill: '#fbfcff',
            connected: '#2d5be3',
            socketFill: '#fbfcff',
            socketHoverFill: '#eef4ff',
            socketFocusFill: '#e9f0ff',
            socketOutline: 'rgba(92, 111, 151, 0.48)',
            socketOutlineMuted: 'rgba(126, 143, 176, 0.32)',
            socketShadow: 'rgba(46, 65, 116, 0.12)',
        },
        eligibleDash: 'rgba(45, 75, 221, 0.45)',
        connection: {
            stroke: '#2d4bdd',
            soft: 'rgba(45, 75, 221, 0.68)',
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
    private materialIconReady = false;
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
            try {
                await fonts.load(`400 20px ${CanvasWrapper.materialIconFontFamilyName}`);
                this.materialIconReady = true;
            } catch {
                this.materialIconReady = false;
            }
            await fonts.ready;
            this.materialIconReady = this.materialIconReady && fonts.check(`400 20px ${CanvasWrapper.materialIconFontFamilyName}`);
            this.fontReady.set(true);
        } catch {
            this.materialIconReady = false;
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
            sourceProperty?: string | null;
        } | null,
        hovered: number | null,
        hoveredPortKey: string | null,
        selectedNodeIds: ReadonlySet<number> = new Set<number>(),
        options?: {
            readOnly?: boolean;
            detailTier?: CanvasDetailTier;
            blockedTargetNodeIds?: ReadonlySet<number>;
            focusedPortKeys?: ReadonlySet<string>;
            collectionIteratorPreviewLabels?: ReadonlyMap<number, string>;
            nodeIssueSummaries?: ReadonlyMap<number, CanvasNodeIssueSummary>;
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
        const collectionIteratorPreviewLabels = options?.collectionIteratorPreviewLabels ?? new Map<number, string>();
        const nodeIssueSummaries = options?.nodeIssueSummaries ?? new Map<number, CanvasNodeIssueSummary>();
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
            const issueSummary = !readOnly ? nodeIssueSummaries.get(node.id) ?? null : null;
            const titleAccessoryRect = readOnly && statusTheme && node.renderStatus
                ? node.settingsRect()
                : issueSummary
                    ? node.settingsRect()
                    : null;

            if (node.isCollectionIteratorOperator) {
                this.drawCollectionIteratorOperator(
                    node,
                    portConnectivity,
                    drag,
                    hovered,
                    hoveredPortKey,
                    isSelected,
                    readOnly,
                    detailTier,
                    blockedTargetNodeIds,
                    focusedPortKeys,
                    validationNodeFocused ? validationFocus : null,
                    validationPortKeys,
                    issueSummary,
                    collectionIteratorPreviewLabels.get(node.id) ?? null,
                );
                continue;
            }

            this.ctx.save();
            this.ctx.shadowColor = detailTier === 'full' ? CanvasWrapper.theme.nodeShadow : 'transparent';
            this.ctx.shadowBlur = detailTier === 'full' ? 10 : 0;
            this.ctx.shadowOffsetY = detailTier === 'full' ? 5 : 0;
            this.ctx.fillStyle = detailTier === 'full'
                ? this.createNodeGradient(node)
                : CanvasWrapper.theme.surface;
            this.ctx.strokeStyle = isSelected
                ? CanvasWrapper.theme.connection.selected
                : statusTheme?.border ?? CanvasWrapper.theme.nodeStroke;
            this.ctx.lineJoin = 'round';
            this.ctx.lineWidth = isSelected ? 2.1 : 1;

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

            this.roundedRect(node.x, node.y, node.width, node.height, 10);
            this.ctx.fill();
            this.ctx.shadowColor = 'transparent';
            this.ctx.shadowBlur = 0;
            if (validationTheme) {
                this.ctx.save();
                this.ctx.fillStyle = validationTheme.surface;
                this.ctx.globalAlpha = 0.22;
                this.roundedRect(node.x + 2, node.y + 2, node.width - 4, node.height - 4, 8);
                this.ctx.fill();
                this.ctx.restore();
            }
            this.ctx.stroke();

            const iconTileX = node.x + 18;
            const iconTileY = node.y + 16;
            const iconTileSize = 30;
            this.ctx.fillStyle = statusTheme?.badgeFill ?? CanvasWrapper.theme.surfaceAccent;
            this.roundedRect(iconTileX, iconTileY, iconTileSize, iconTileSize, 8);
            this.ctx.fill();
            this.ctx.strokeStyle = statusTheme?.border ?? 'rgba(151, 166, 204, 0.30)';
            this.ctx.lineWidth = 1;
            this.roundedRect(iconTileX + 0.5, iconTileY + 0.5, iconTileSize - 1, iconTileSize - 1, 7.5);
            this.ctx.stroke();

            this.drawTaskPluginIcon(iconTileX, iconTileY, iconTileSize, iconTileSize, statusTheme?.accent ?? CanvasWrapper.theme.accent);

            const titleX = node.x + 66;
            const titleMaxWidth = titleAccessoryRect
                ? Math.max(24, titleAccessoryRect.x - titleX - 10)
                : layout.titleMaxWidth;
            this.ctx.fillStyle = CanvasWrapper.theme.nodeText;
            this.setFont(700, 15);
            this.drawEllipsizedText(
                node.name,
                titleX,
                layout.titleBaseline,
                titleMaxWidth,
            );

            if (issueSummary && titleAccessoryRect) {
                this.drawNodeIssueIcon(titleAccessoryRect, issueSummary);
            }

            this.ctx.fillStyle = CanvasWrapper.theme.nodeTextMuted;
            this.setFont(500, 11);
            this.drawEllipsizedText(
                node.resolvedVersion ?? '0.0.1',
                titleX,
                layout.versionBaseline,
                titleMaxWidth,
            );

            if (readOnly && statusTheme && node.renderStatus) {
                this.drawNodeStatusIcon(node, node.renderStatus, statusTheme.badgeFill, statusTheme.text, statusTheme.accent);
            }

            if (!readOnly && node.hasDesignTimeOverrides) {
                this.drawNodeOverrideBadge(node, `${node.designTimeOverrideCount} override${node.designTimeOverrideCount === 1 ? '' : 's'}`);
            }

            if (layout.inputSection) {
                this.drawChannelSection(node, layout.inputSection, hovered === node.id, hoveredPortKey, connectedSummaryPortKeys, connectedPropertyPortKeys, drag?.from === node.id ? drag.portKey ?? null : null, focusedPortKeys, validationNodeFocused ? validationFocus : null, validationPortKeys, issueSummary);
            }
            layout.outputSections.forEach((section) => {
                this.drawChannelSection(node, section, hovered === node.id, hoveredPortKey, connectedSummaryPortKeys, connectedPropertyPortKeys, drag?.from === node.id ? drag.portKey ?? null : null, focusedPortKeys, validationNodeFocused ? validationFocus : null, validationPortKeys, issueSummary);
            });

            this.ctx.restore();
        }
    }

    private drawCollectionIteratorOperator(
        node: RenderedNode,
        portConnectivity: CanvasPortConnectivityIndex,
        drag: {
            from: number;
            anchor: number;
            x: number;
            y: number;
            side?: Side;
            portKey?: string | null;
            sourceProperty?: string | null;
        } | null,
        hovered: number | null,
        hoveredPortKey: string | null,
        isSelected: boolean,
        readOnly: boolean,
        detailTier: CanvasDetailTier,
        blockedTargetNodeIds: ReadonlySet<number>,
        focusedPortKeys: ReadonlySet<string>,
        validationFocus: {
            severity: CanvasValidationSeverity;
            nodeId: number | null;
            sectionKey?: string | null;
            portKeys?: ReadonlySet<string>;
        } | null,
        validationPortKeys: ReadonlySet<string>,
        issueSummary: CanvasNodeIssueSummary | null,
        previewEquation: string | null,
    ): void {
        const statusTheme = node.renderStatus ? CanvasWrapper.theme.status[node.renderStatus] : null;
        const validationTheme = validationFocus ? CanvasWrapper.theme.validation[validationFocus.severity] : null;
        const isHoveredDest = !!drag && node.id !== drag.from && hovered === node.id;
        const isBlockedDest = !!drag && blockedTargetNodeIds.has(node.id);
        const eligible = !!drag && node.id !== drag.from && !isBlockedDest;
        const isResolved = node.base.collectionIteratorResolved || previewEquation !== null;
        const equation = node.base.collectionIteratorResolved
            ? node.base.collectionIteratorEquation
            : previewEquation ?? node.base.collectionIteratorEquation;

        this.ctx.save();
        this.ctx.shadowColor = detailTier === 'full' ? CanvasWrapper.theme.nodeShadow : 'transparent';
        this.ctx.shadowBlur = detailTier === 'full' ? 12 : 0;
        this.ctx.shadowOffsetY = detailTier === 'full' ? 6 : 0;
        this.ctx.fillStyle = detailTier === 'full'
            ? this.createCollectionIteratorGradient(node, isResolved)
            : CanvasWrapper.theme.surface;
        this.ctx.strokeStyle = isSelected
            ? CanvasWrapper.theme.connection.selected
            : statusTheme?.border ?? (isResolved ? 'rgba(45, 75, 221, 0.28)' : CanvasWrapper.theme.nodeStroke);
        this.ctx.lineJoin = 'round';
        this.ctx.lineWidth = isSelected ? 2.25 : 1.15;

        if (validationTheme) {
            this.ctx.strokeStyle = validationTheme.stroke;
            this.ctx.lineWidth = 1.8;
        } else if (isHoveredDest && isBlockedDest) {
            this.ctx.strokeStyle = 'rgba(217, 45, 32, 0.62)';
            this.ctx.setLineDash([4, 4]);
        } else if (isHoveredDest) {
            this.ctx.strokeStyle = CanvasWrapper.theme.nodeStrokeHover;
            this.ctx.setLineDash([5, 4]);
        } else if (eligible) {
            this.ctx.strokeStyle = CanvasWrapper.theme.eligibleDash;
            this.ctx.globalAlpha = 0.58;
        }

        this.collectionIteratorOperatorPath(node.x, node.y, node.width, node.height);
        this.ctx.fill();
        this.ctx.shadowColor = 'transparent';
        this.ctx.shadowBlur = 0;

        if (validationTheme) {
            this.ctx.save();
            this.ctx.fillStyle = validationTheme.surface;
            this.ctx.globalAlpha = 0.22;
            this.collectionIteratorOperatorPath(node.x + 2, node.y + 2, node.width - 4, node.height - 4);
            this.ctx.fill();
            this.ctx.restore();
        }

        this.collectionIteratorOperatorPath(node.x, node.y, node.width, node.height);
        this.ctx.stroke();
        this.ctx.setLineDash([]);
        this.ctx.globalAlpha = 1;

        const centerX = node.x + node.width / 2;
        const label = 'Collection Iterator';
        this.setFont(700, 10);
        const labelWidth = this.ctx.measureText(label).width;
        const iconSize = 18;
        const labelGroupWidth = iconSize + 7 + labelWidth;
        const labelGroupX = centerX - labelGroupWidth / 2;
        const labelY = node.y + 31;

        this.drawCollectionIteratorIcon(
            labelGroupX,
            labelY - iconSize + 4,
            iconSize,
            isResolved ? CanvasWrapper.theme.accent : CanvasWrapper.theme.nodeTextMuted,
        );

        this.ctx.fillStyle = CanvasWrapper.theme.nodeTextMuted;
        this.setFont(700, 10);
        this.ctx.fillText(label, labelGroupX + iconSize + 7, labelY);

        this.ctx.fillStyle = isResolved
            ? CanvasWrapper.theme.accent
            : CanvasWrapper.theme.nodeText;
        this.setFont(700, 18);
        this.ctx.textAlign = 'center';
        this.ctx.textBaseline = 'alphabetic';
        this.ctx.fillText(equation, centerX, node.y + 67);

        this.ctx.fillStyle = isResolved
            ? 'rgba(45, 75, 221, 0.72)'
            : CanvasWrapper.theme.nodeTextMuted;
        this.setFont(700, 8.5);
        this.ctx.textAlign = 'left';
        this.ctx.fillText('COLLECTION', node.x + 24, node.y + 91);
        this.ctx.textAlign = 'right';
        this.ctx.fillText('ITEM', node.x + node.width - 24, node.y + 91);
        this.ctx.textAlign = 'start';

        if (issueSummary) {
            this.drawNodeIssueIcon(node.settingsRect(), issueSummary);
        }

        if (readOnly && statusTheme && node.renderStatus) {
            this.drawNodeStatusIcon(node, node.renderStatus, statusTheme.badgeFill, statusTheme.text, statusTheme.accent);
        }

        const draggingPortKey = drag?.from === node.id ? drag.portKey ?? null : null;
        const inputSection = node.layout.inputSection;
        const outputSection = node.layout.outputSections[0] ?? null;
        if (inputSection) {
            const inputSummaryKey = this.portFocusKey(node, inputSection.summaryPort);
            const inputValidationFocused = validationPortKeys.has(inputSummaryKey) ||
                validationFocus?.sectionKey === inputSection.key;
            const inputFocused = focusedPortKeys.has(inputSummaryKey) || inputValidationFocused;
            const inputHovered = (hovered === node.id && hoveredPortKey === inputSection.summaryPort.key) ||
                inputFocused ||
                (isHoveredDest && !isBlockedDest);
            this.drawSummarySocket(
                inputSection,
                inputHovered,
                portConnectivity.summaryPortKeys.has(inputSummaryKey),
                inputValidationFocused ? validationFocus?.severity ?? null : null,
            );
        }

        if (outputSection) {
            const outputSummaryKey = this.portFocusKey(node, outputSection.summaryPort);
            const outputValidationFocused = validationPortKeys.has(outputSummaryKey) ||
                validationFocus?.sectionKey === outputSection.key;
            const outputFocused = focusedPortKeys.has(outputSummaryKey) || outputValidationFocused;
            const outputHovered = (hovered === node.id && hoveredPortKey === outputSection.summaryPort.key) ||
                outputFocused ||
                draggingPortKey === outputSection.summaryPort.key;
            this.drawSummarySocket(
                outputSection,
                outputHovered,
                portConnectivity.summaryPortKeys.has(outputSummaryKey) || draggingPortKey === outputSection.summaryPort.key,
                outputValidationFocused ? validationFocus?.severity ?? null : null,
            );
        }

        this.ctx.restore();
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
        issueSummary: CanvasNodeIssueSummary | null,
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

        const sectionX = node.x + 16;
        const sectionWidth = node.width - 32;
        const sectionHeight = Math.max(section.headerHeight, section.bottom - section.headerTop);
        const sectionRadius = 7;
        const rowCenterY = section.headerTop + section.headerHeight / 2;

        this.ctx.fillStyle = CanvasWrapper.theme.sectionSurfaceAlt;
        this.roundedRect(sectionX, section.headerTop, sectionWidth, sectionHeight, sectionRadius);
        this.ctx.fill();

        const headerFill = summaryValidationFocused && validationTheme
            ? validationTheme.surface
            : summaryActive
            ? CanvasWrapper.theme.sectionSurfaceActive
            : CanvasWrapper.theme.sectionSurface;
        this.ctx.fillStyle = headerFill;
        this.roundedRect(sectionX, section.headerTop, sectionWidth, section.headerHeight, sectionRadius);
        this.ctx.fill();

        this.ctx.strokeStyle = summaryValidationFocused && validationTheme
            ? validationTheme.stroke
            : summaryActive
            ? CanvasWrapper.theme.sectionBorderActive
            : CanvasWrapper.theme.sectionBorder;
        this.ctx.lineWidth = summaryValidationFocused ? 1.7 : summaryActive ? 1.2 : 1;
        this.roundedRect(sectionX, section.headerTop, sectionWidth, sectionHeight, sectionRadius);
        this.ctx.stroke();

        const countLabel = this.compactPropertyCountLabel(section.countLabel).toUpperCase();
        this.setFont(700, 8.5);
        const countWidth = this.ctx.measureText(countLabel).width;
        const countChipPaddingX = 6;
        const countChipHeight = 16;
        const countChipWidth = countWidth + countChipPaddingX * 2;
        const countChipX = section.toggleRect.x - 8 - countChipWidth;
        const countChipY = rowCenterY - countChipHeight / 2;

        this.ctx.fillStyle = summaryConnected || summaryActive || summaryValidationFocused
            ? 'rgba(45, 75, 221, 0.09)'
            : 'rgba(113, 131, 166, 0.08)';
        this.roundedRect(countChipX, countChipY, countChipWidth, countChipHeight, 5);
        this.ctx.fill();

        this.ctx.strokeStyle = summaryConnected || summaryActive || summaryValidationFocused
            ? 'rgba(45, 75, 221, 0.22)'
            : 'rgba(151, 166, 204, 0.24)';
        this.ctx.lineWidth = 1;
        this.roundedRect(countChipX + 0.5, countChipY + 0.5, countChipWidth - 1, countChipHeight - 1, 4.5);
        this.ctx.stroke();

        this.ctx.fillStyle = summaryConnected || summaryActive || summaryValidationFocused
            ? CanvasWrapper.theme.accent
            : CanvasWrapper.theme.nodeTextMuted;
        this.ctx.fillText(countLabel, countChipX + countChipPaddingX, rowCenterY + 3);

        this.ctx.fillStyle = summaryActive || summaryValidationFocused
            ? CanvasWrapper.theme.nodeText
            : CanvasWrapper.theme.nodeSectionText;
        this.setFont(700, 10);
        const sectionLabelX = node.x + 34;
        this.drawEllipsizedText(
            section.label.toUpperCase(),
            sectionLabelX,
            rowCenterY + 4,
            Math.max(48, countChipX - sectionLabelX - 12),
        );
        this.drawSectionToggle(section);

        if (section.propertyRows.length > 0) {
            this.ctx.strokeStyle = CanvasWrapper.theme.sectionDivider;
            this.ctx.lineWidth = 1;
            this.ctx.beginPath();
            this.ctx.moveTo(sectionX + 10, section.headerTop + section.headerHeight);
            this.ctx.lineTo(sectionX + sectionWidth - 10, section.headerTop + section.headerHeight);
            this.ctx.stroke();

            for (const row of section.propertyRows) {
                const rowScopedKey = this.portFocusKey(node, row.port);
                const rowValidationFocused = validationPortKeys.has(rowScopedKey);
                const isFocusedEndpoint = focusedPortKeys.has(rowScopedKey);
                const isHover = (isHoveredNode && hoveredPortKey === row.port.key) || isFocusedEndpoint || rowValidationFocused;
                const isConnected = connectedPropertyPortKeys.has(rowScopedKey);
                const propertyIssue = issueSummary?.propertyIssues.get(this.propertyIssueKey(row.port)) ?? null;

                this.ctx.fillStyle = rowValidationFocused && validationTheme
                    ? validationTheme.fill
                    : isHover || isFocusedEndpoint
                    ? CanvasWrapper.theme.sectionRowActive
                    : CanvasWrapper.theme.sectionRowSurface;
                this.fillRect(sectionX + 1, row.top, sectionWidth - 2, row.height);

                if (row !== section.propertyRows[0]) {
                    this.ctx.strokeStyle = CanvasWrapper.theme.sectionDivider;
                    this.ctx.lineWidth = 1;
                    this.ctx.beginPath();
                    this.ctx.moveTo(sectionX + 12, row.top);
                    this.ctx.lineTo(sectionX + sectionWidth - 12, row.top);
                    this.ctx.stroke();
                }

                const propertyType = row.displayKind.toUpperCase();
                this.setFont(600, 8.5);
                const propertyTypeWidth = this.ctx.measureText(propertyType).width;
                const propertyTypeX = node.x + node.width - 36 - propertyTypeWidth;
                const propertyIssueSpace = propertyIssue ? 14 : 0;
                const propertyIssueX = propertyTypeX - 9;
                const labelX = node.x + 34;
                const labelMaxWidth = Math.max(40, propertyTypeX - labelX - propertyIssueSpace - (row.required ? 18 : 8));

                this.ctx.fillStyle = CanvasWrapper.theme.nodeText;
                this.setFont(600, 11.5);
                const displayedLabel = this.drawEllipsizedText(row.label, labelX, row.centerY + 4, labelMaxWidth);

                if (row.required) {
                    this.ctx.fillStyle = CanvasWrapper.theme.accent;
                    this.setFont(700, 11.5);
                    const labelWidth = Math.min(this.ctx.measureText(displayedLabel).width, labelMaxWidth);
                    this.ctx.fillText('*', labelX + labelWidth + 4, row.centerY + 4);
                }

                this.ctx.fillStyle = CanvasWrapper.theme.nodePropertyType;
                this.setFont(600, 8.5);
                this.ctx.fillText(propertyType, propertyTypeX, row.centerY + 3);

                if (propertyIssue) {
                    this.drawInlineIssueIcon(propertyIssueX, row.centerY, propertyIssue.severity);
                }

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

        this.drawSummarySocket(
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
        this.ctx.fillStyle = section.expanded ? 'rgba(45, 75, 221, 0.08)' : 'rgba(16, 23, 40, 0.02)';
        this.roundedRect(rect.x, rect.y, rect.width, rect.height, 6);
        this.ctx.fill();

        this.ctx.strokeStyle = section.expanded ? 'rgba(45, 75, 221, 0.62)' : 'rgba(115, 132, 170, 0.34)';
        this.ctx.lineWidth = 1.2;
        this.roundedRect(rect.x + 0.5, rect.y + 0.5, rect.width - 1, rect.height - 1, 5.5);
        this.ctx.stroke();

        this.ctx.strokeStyle = section.expanded ? CanvasWrapper.theme.accent : 'rgba(88, 106, 146, 0.58)';
        this.ctx.lineWidth = 1.45;
        this.ctx.lineCap = 'round';
        this.ctx.beginPath();
        this.ctx.moveTo(centerX - 4.5, centerY);
        this.ctx.lineTo(centerX + 4.5, centerY);
        this.ctx.stroke();

        if (!section.expanded) {
            this.ctx.beginPath();
            this.ctx.moveTo(centerX, centerY - 4.5);
            this.ctx.lineTo(centerX, centerY + 4.5);
            this.ctx.stroke();
        }

        this.ctx.restore();
    }

    private portFocusKey(node: RenderedNode, port: NodePortDefinition): string {
        return `${node.id}:${port.key}`;
    }

    private compactPropertyCountLabel(label: string): string {
        return label.replace(' properties', ' props').replace(' property', ' prop');
    }

    private createNodeGradient(node: RenderedNode): CanvasGradient {
        const gradient = this.ctx.createLinearGradient(node.x, node.y, node.x + node.width, node.y + node.height);
        gradient.addColorStop(0, CanvasWrapper.theme.surface);
        gradient.addColorStop(1, CanvasWrapper.theme.surfaceAlt);
        return gradient;
    }

    private createCollectionIteratorGradient(node: RenderedNode, resolved: boolean): CanvasGradient {
        const gradient = this.ctx.createLinearGradient(node.x, node.y, node.x + node.width, node.y + node.height);
        if (resolved) {
            gradient.addColorStop(0, '#fbfdff');
            gradient.addColorStop(1, '#eef5ff');
        } else {
            gradient.addColorStop(0, CanvasWrapper.theme.surface);
            gradient.addColorStop(1, CanvasWrapper.theme.surfaceAlt);
        }

        return gradient;
    }

    private collectionIteratorOperatorPath(x: number, y: number, width: number, height: number): void {
        const chamfer = Math.min(22, width * 0.11, height * 0.32);
        const centerY = y + height / 2;

        this.ctx.beginPath();
        this.ctx.moveTo(x + chamfer, y);
        this.ctx.lineTo(x + width - chamfer, y);
        this.ctx.lineTo(x + width, centerY);
        this.ctx.lineTo(x + width - chamfer, y + height);
        this.ctx.lineTo(x + chamfer, y + height);
        this.ctx.lineTo(x, centerY);
        this.ctx.closePath();
    }

    private drawTaskPluginIcon(x: number, y: number, width: number, height: number, color: string): void {
        const centerX = x + width / 2;
        const centerY = y + height / 2;

        this.ctx.save();
        this.ctx.fillStyle = color;

        if (this.materialIconReady) {
            this.ctx.font = `400 20px ${CanvasWrapper.materialIconFontFamily}`;
            this.ctx.textAlign = 'center';
            this.ctx.textBaseline = 'middle';
            this.ctx.fillText('extension', centerX, centerY + 0.5);
        } else {
            this.drawFallbackPuzzleIcon(centerX, centerY, color);
        }

        this.ctx.restore();
    }

    private drawCollectionIteratorIcon(x: number, y: number, size: number, color: string): void {
        const centerX = x + size / 2;
        const centerY = y + size / 2;

        this.ctx.save();
        this.ctx.fillStyle = color;
        this.ctx.strokeStyle = color;

        if (this.materialIconReady) {
            this.ctx.font = `400 ${Math.round(size + 2)}px ${CanvasWrapper.materialIconFontFamily}`;
            this.ctx.textAlign = 'center';
            this.ctx.textBaseline = 'middle';
            this.ctx.fillText('call_split', centerX, centerY + 0.5);
            this.ctx.restore();
            return;
        }

        this.ctx.lineWidth = 1.7;
        this.ctx.lineCap = 'round';
        this.ctx.lineJoin = 'round';
        this.ctx.beginPath();
        this.ctx.moveTo(x + 2.5, centerY);
        this.ctx.lineTo(centerX - 1, centerY);
        this.ctx.lineTo(x + size - 3, y + 4);
        this.ctx.moveTo(centerX - 1, centerY);
        this.ctx.lineTo(x + size - 3, y + size - 4);
        this.ctx.stroke();

        this.ctx.lineWidth = 1.2;
        this.ctx.beginPath();
        this.ctx.moveTo(x + 1.5, y + 3);
        this.ctx.lineTo(x + 1.5, y + size - 3);
        this.ctx.moveTo(x + size - 1.5, y + 3);
        this.ctx.lineTo(x + size - 1.5, y + size - 3);
        this.ctx.stroke();
        this.ctx.restore();
    }

    private drawFallbackPuzzleIcon(centerX: number, centerY: number, color: string): void {
        const size = 15;
        const x = centerX - size / 2;
        const y = centerY - size / 2;

        this.ctx.save();
        this.ctx.fillStyle = color;
        this.roundedRect(x, y + 2, size, size - 2, 3);
        this.ctx.fill();

        this.ctx.globalCompositeOperation = 'destination-out';
        this.ctx.beginPath();
        this.ctx.arc(centerX, y + 2, 2.6, 0, Math.PI * 2);
        this.ctx.arc(x + size, centerY + 1, 2.6, 0, Math.PI * 2);
        this.ctx.fill();
        this.ctx.restore();
    }

    private fillRect(x: number, y: number, width: number, height: number): void {
        this.ctx.beginPath();
        this.ctx.rect(x, y, width, height);
        this.ctx.fill();
    }

    private drawSummarySocket(
        section: NodeChannelSectionLayout,
        isHover: boolean,
        isConnected: boolean,
        validationSeverity: CanvasValidationSeverity | null = null,
    ): void {
        const port = section.summaryPort;
        const validationTheme = validationSeverity ? CanvasWrapper.theme.validation[validationSeverity] : null;
        const radius = port.radius;
        const haloRadius = radius + 6;

        this.ctx.save();

        if (isHover || isConnected || validationTheme) {
            this.ctx.fillStyle = validationTheme?.surface ?? 'rgba(31, 73, 255, 0.11)';
            this.ctx.beginPath();
            this.ctx.arc(port.x, port.y, haloRadius, 0, Math.PI * 2);
            this.ctx.fill();
        }

        this.ctx.shadowColor = isHover || isConnected
            ? validationTheme?.glow ?? 'rgba(45, 75, 221, 0.18)'
            : CanvasWrapper.theme.anchor.socketShadow;
        this.ctx.shadowBlur = isHover || isConnected ? 8 : 3;
        this.ctx.shadowOffsetY = 1.5;

        this.ctx.fillStyle = isHover || isConnected
            ? validationTheme?.fill ?? CanvasWrapper.theme.anchor.socketFocusFill
            : CanvasWrapper.theme.anchor.socketFill;
        this.ctx.beginPath();
        this.ctx.arc(port.x, port.y, radius, 0, Math.PI * 2);
        this.ctx.fill();

        this.ctx.shadowColor = 'transparent';
        this.ctx.shadowBlur = 0;
        this.ctx.shadowOffsetY = 0;

        this.ctx.strokeStyle = isHover || isConnected
            ? validationTheme?.stroke ?? CanvasWrapper.theme.anchor.connected
            : CanvasWrapper.theme.anchor.socketOutline;
        this.ctx.lineWidth = isHover || isConnected ? 1.9 : 1.25;
        this.ctx.beginPath();
        this.ctx.arc(port.x, port.y, radius, 0, Math.PI * 2);
        this.ctx.stroke();

        if (isConnected) {
            this.ctx.fillStyle = validationTheme?.stroke ?? CanvasWrapper.theme.anchor.connected;
            this.ctx.beginPath();
            this.ctx.arc(port.x, port.y, 2.45, 0, Math.PI * 2);
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
        const activeRadius = radius + 5.5;

        this.ctx.save();

        if (isHover || isGrabbed || validationTheme) {
            this.ctx.beginPath();
            this.ctx.fillStyle = isGrabbed
                ? CanvasWrapper.theme.anchor.connected
                : validationTheme?.surface ?? 'rgba(31, 73, 255, 0.13)';
            this.ctx.arc(x, y, activeRadius, 0, 2 * Math.PI);
            this.ctx.fill();
        }

        this.ctx.fillStyle = isHover || isGrabbed || isConnected
            ? validationTheme?.fill ?? CanvasWrapper.theme.anchor.socketFocusFill
            : CanvasWrapper.theme.anchor.socketFill;
        this.ctx.beginPath();
        this.ctx.arc(x, y, radius, 0, 2 * Math.PI);
        this.ctx.fill();

        this.ctx.strokeStyle = isHover || isGrabbed || isConnected
            ? validationTheme?.stroke ?? CanvasWrapper.theme.anchor.connected
            : CanvasWrapper.theme.anchor.socketOutlineMuted;
        this.ctx.lineWidth = isHover || isGrabbed || isConnected ? 1.65 : 1.1;
        this.ctx.beginPath();
        this.ctx.arc(x, y, radius, 0, 2 * Math.PI);
        this.ctx.stroke();

        if (isConnected || isGrabbed) {
            this.ctx.fillStyle = validationTheme?.stroke ?? CanvasWrapper.theme.anchor.connected;
            this.ctx.beginPath();
            this.ctx.arc(x, y, isGrabbed ? 2.4 : 1.9, 0, 2 * Math.PI);
            this.ctx.fill();
        }

        this.ctx.restore();
    }

    private drawNodeIssueIcon(rect: { x: number; y: number; width: number; height: number }, issue: CanvasNodeIssueSummary): void {
        const theme = CanvasWrapper.theme.validation[issue.severity];
        const centerX = rect.x + rect.width / 2;
        const centerY = rect.y + rect.height / 2;

        this.ctx.save();
        this.ctx.fillStyle = issue.severity === 'error'
            ? 'rgba(255, 232, 229, 0.82)'
            : 'rgba(255, 247, 220, 0.86)';
        this.roundedRect(rect.x, rect.y, rect.width, rect.height, 6);
        this.ctx.fill();

        this.ctx.strokeStyle = theme.stroke;
        this.ctx.globalAlpha = 0.28;
        this.ctx.lineWidth = 1.2;
        this.roundedRect(rect.x + 0.6, rect.y + 0.6, rect.width - 1.2, rect.height - 1.2, 5.5);
        this.ctx.stroke();

        this.ctx.globalAlpha = 1;
        this.ctx.fillStyle = theme.stroke;
        if (this.materialIconReady) {
            this.ctx.font = `400 17px ${CanvasWrapper.materialIconFontFamily}`;
            this.ctx.textAlign = 'center';
            this.ctx.textBaseline = 'middle';
            this.ctx.fillText(issue.severity === 'error' ? 'error' : 'warning', centerX, centerY + 0.5);
        } else {
            this.setFont(700, 13);
            this.ctx.textAlign = 'center';
            this.ctx.textBaseline = 'middle';
            this.ctx.fillText(issue.severity === 'error' ? '!' : '!', centerX, centerY + 0.5);
        }

        this.ctx.restore();
    }

    private drawInlineIssueIcon(centerX: number, centerY: number, severity: CanvasValidationSeverity): void {
        const theme = CanvasWrapper.theme.validation[severity];

        this.ctx.save();
        this.ctx.fillStyle = theme.stroke;
        if (this.materialIconReady) {
            this.ctx.font = `400 11px ${CanvasWrapper.materialIconFontFamily}`;
            this.ctx.textAlign = 'center';
            this.ctx.textBaseline = 'middle';
            this.ctx.fillText(severity === 'error' ? 'error' : 'warning', centerX, centerY + 0.5);
        } else {
            this.setFont(700, 9);
            this.ctx.textAlign = 'center';
            this.ctx.textBaseline = 'middle';
            this.ctx.fillText('!', centerX, centerY + 0.5);
        }
        this.ctx.restore();
    }

    private propertyIssueKey(port: NodePortDefinition): string {
        return `${port.side}:${port.channelName ?? ''}:${port.propertyName ?? ''}`;
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
        this.roundedRect(rect.x, rect.y, rect.width, rect.height, 6);
        this.ctx.fill();

        this.ctx.strokeStyle = accent;
        this.ctx.globalAlpha = 0.34;
        this.ctx.lineWidth = 1.4;
        this.roundedRect(rect.x + 0.7, rect.y + 0.7, rect.width - 1.4, rect.height - 1.4, 5.5);
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

    drawInteractionTooltip(label: string, point: Point): void {
        const normalizedLabel = label.trim();
        if (!normalizedLabel) {
            return;
        }

        this.ctx.save();
        const scale = Math.max(this.zoomLevel(), 0.2);
        this.setFont(600, 11 / scale);
        const paddingX = 9 / scale;
        const height = 24 / scale;
        const maxWidth = 220 / scale;
        const displayLabel = this.ellipsizeText(normalizedLabel, maxWidth - paddingX * 2);
        const textWidth = this.ctx.measureText(displayLabel).width;
        const width = Math.min(maxWidth, Math.max(48 / scale, textWidth + paddingX * 2));
        const x = point.x - width / 2;
        const y = point.y - height - 8 / scale;

        this.ctx.fillStyle = CanvasWrapper.theme.tooltipBg;
        this.roundedRect(x, y, width, height, 7 / scale);
        this.ctx.fill();

        this.ctx.fillStyle = CanvasWrapper.theme.tooltipText;
        this.ctx.textAlign = 'center';
        this.ctx.textBaseline = 'middle';
        this.ctx.fillText(displayLabel, x + width / 2, y + height / 2 + 0.5 / scale);
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

        this.ctx.save();
        this.ctx.strokeStyle = route.status === 'obstacleViolation'
            ? CanvasWrapper.theme.validation.error.stroke
            : CanvasWrapper.theme.connection.selected;
        this.ctx.fillStyle = this.ctx.strokeStyle;
        this.ctx.lineWidth = route.status === 'obstacleViolation' ? 5.4 : 6.2;
        this.ctx.lineJoin = 'round';
        this.ctx.lineCap = 'butt';

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

        this.ctx.restore();
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
