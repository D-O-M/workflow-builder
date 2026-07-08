import {
    CanvasConnectionPresentation,
    CanvasDetailTier,
    CanvasValidationSeverity,
    RenderedConnection,
} from './canvas-render-model';
import { drawRenderedConnection } from './canvas-connection-drawing-utility';
import { Canvas2DContext } from './canvas-draw-surface';

interface CanvasConnectionRenderTarget {
    readonly ctx: Canvas2DContext;
}

export interface CanvasConnectionRenderOptions {
    readonly readOnly?: boolean;
    readonly detailTier?: CanvasDetailTier;
    readonly connectionPresentation?: CanvasConnectionPresentation;
    readonly hoveredConnectionId?: string | null;
    readonly hoveredConnectionRouteIndex?: number | null;
    readonly channelFocusedConnectionIds?: ReadonlySet<string>;
    readonly validationConnectionFocus?: {
        readonly connectionId: string;
        readonly routeIndex?: number | null;
        readonly severity: CanvasValidationSeverity;
    } | null;
}

export class CanvasConnectionRenderer {
    constructor(private readonly target: CanvasConnectionRenderTarget) {}

    drawConnections(
        connections: readonly RenderedConnection[],
        selectedConnectionIds: ReadonlySet<string> = new Set<string>(),
        options?: CanvasConnectionRenderOptions,
    ): void {
        const hoveredConnectionId = options?.hoveredConnectionId ?? null;
        const hoveredConnectionRouteIndex =
            options?.hoveredConnectionRouteIndex ?? null;
        const connectionPresentation =
            options?.connectionPresentation ?? 'normal';
        const channelFocusedConnectionIds =
            options?.channelFocusedConnectionIds ?? new Set<string>();
        const validationConnectionFocus =
            options?.validationConnectionFocus ?? null;
        const validationConnectionId =
            validationConnectionFocus?.connectionId ?? null;
        const hasConnectionFocus =
            hoveredConnectionId !== null ||
            channelFocusedConnectionIds.size > 0 ||
            validationConnectionId !== null;
        const focusedConnections: RenderedConnection[] = [];

        for (const connection of connections) {
            const selected = selectedConnectionIds.has(connection.id);
            const hovered =
                hoveredConnectionId === connection.id ||
                channelFocusedConnectionIds.has(connection.id) ||
                validationConnectionId === connection.id;
            if (selected || hovered) {
                focusedConnections.push(connection);
                continue;
            }

            drawRenderedConnection(connection, this.target, {
                selected: false,
                hovered: false,
                dimmed: hasConnectionFocus,
                presentation: connectionPresentation,
            });
        }

        for (const connection of focusedConnections) {
            const directlyHovered = hoveredConnectionId === connection.id;
            const validationFocused =
                validationConnectionFocus?.connectionId === connection.id;
            drawRenderedConnection(connection, this.target, {
                selected: selectedConnectionIds.has(connection.id),
                hovered:
                    directlyHovered ||
                    channelFocusedConnectionIds.has(connection.id),
                hoveredRouteIndex: directlyHovered
                    ? hoveredConnectionRouteIndex
                    : validationFocused
                      ? (validationConnectionFocus.routeIndex ?? null)
                      : null,
                validationSeverity: validationFocused
                    ? validationConnectionFocus.severity
                    : null,
                dimmed: false,
                presentation: connectionPresentation,
            });
        }
    }
}
