import { CanvasBackgroundEffect } from './canvas-backdrop';
import { CanvasDragConnectionState } from './canvas-interaction-controller';
import { CanvasRenderCullPlan } from './canvas-render-culling';
import { CanvasRenderFramePlan } from './canvas-render-frame-factory';
import { CanvasRenderSnapshot } from './canvas-render-model';
import { CanvasWrapper } from './canvas-wrapper';
import { Point } from './node-model';

export interface CanvasRenderFrameRenderRequest {
    readonly wrapper: CanvasWrapper;
    readonly canvasWidth: number;
    readonly canvasHeight: number;
    readonly canvasClientWidth: number;
    readonly canvasClientHeight: number;
    readonly offset: Point;
    readonly zoom: number;
    readonly backgroundEffect: CanvasBackgroundEffect;
    readonly backdropHoverCanvasPoint: Point | null;
    readonly snapshot: CanvasRenderSnapshot;
    readonly frame: CanvasRenderFramePlan;
    readonly cullPlan: CanvasRenderCullPlan;
    readonly dragConnection: CanvasDragConnectionState | null;
    readonly hoveredNodeId: number | null;
    readonly hoveredPortKey: string | null;
}

export function renderCanvasFrame(
    request: CanvasRenderFrameRenderRequest,
): void {
    const { wrapper } = request;
    const context = wrapper.ctx;
    wrapper.clear(request.canvasWidth, request.canvasHeight);

    context.save();
    context.translate(request.offset.x, request.offset.y);
    context.scale(request.zoom, request.zoom);
    wrapper.drawBackdrop(
        request.canvasClientWidth,
        request.canvasClientHeight,
        request.backgroundEffect,
        request.backdropHoverCanvasPoint,
    );

    wrapper.drawConnections(
        request.cullPlan.connections,
        request.frame.connectionLayer.selectedConnectionIds,
        request.frame.connectionLayer.options,
    );
    wrapper.drawNodes(
        request.cullPlan.nodes,
        request.snapshot.portConnectivity,
        request.dragConnection,
        request.hoveredNodeId,
        request.hoveredPortKey,
        request.frame.nodeLayer.selectedNodeIds,
        request.frame.nodeLayer.options,
    );

    const preview = request.frame.connectionPreview;
    if (preview) {
        wrapper.drawOrthogonalLine(
            preview.sx,
            preview.sy,
            preview.ex,
            preview.ey,
            [...preview.obstacles],
            preview.side,
            preview.destNode,
            preview.startSide,
            preview.startNode,
        );
    }

    if (request.frame.marqueeBounds) {
        wrapper.drawMarqueeRect(
            request.frame.marqueeBounds.left,
            request.frame.marqueeBounds.top,
            request.frame.marqueeBounds.right,
            request.frame.marqueeBounds.bottom,
        );
    }

    if (request.frame.failureTooltip) {
        wrapper.drawFailureTooltip(
            request.frame.failureTooltip.node,
            request.frame.failureTooltip.message,
        );
    }

    if (request.frame.hoverTooltip) {
        wrapper.drawInteractionTooltip(
            request.frame.hoverTooltip.label,
            request.frame.hoverTooltip.point,
        );
    }

    context.restore();
}
