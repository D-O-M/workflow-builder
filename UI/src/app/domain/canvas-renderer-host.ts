import { CanvasBackgroundEffect } from './canvas-backdrop';
import { CanvasDragConnectionState } from './canvas-interaction-controller';
import { CanvasRenderCullPlan } from './canvas-render-culling';
import { CanvasRenderFramePlan } from './canvas-render-frame-factory';
import { renderCanvasFrame } from './canvas-render-frame-renderer';
import {
    CanvasWorkerHostMessage,
    serializeCanvasWorkerFrame,
} from './canvas-render-worker-contract';
import { CanvasRenderSnapshot } from './canvas-render-model';
import { CanvasWrapper } from './canvas-wrapper';
import { Point } from './node-model';

export interface CanvasRendererHostResize {
    readonly width: number;
    readonly height: number;
    readonly devicePixelRatio: number;
}

export interface CanvasRendererFrameRequest {
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

export interface CanvasRendererHost {
    readonly kind: 'main-thread' | 'offscreen';
    readonly submittedFrameId: number;
    readonly presentedFrameId: number;
    readonly lastSubmittedNodeCount: number;
    readonly lastSubmittedConnectionCount: number;
    resize(request: CanvasRendererHostResize): void;
    updateFontState(font: {
        readonly fontFamily: string;
        readonly fontReady: boolean;
        readonly materialIconReady: boolean;
    }): void;
    drawFrame(request: CanvasRendererFrameRequest): void;
    destroy(): void;
}

export class MainThreadCanvasRendererHost implements CanvasRendererHost {
    readonly kind = 'main-thread' as const;
    submittedFrameId = 0;
    presentedFrameId = 0;
    lastSubmittedNodeCount = 0;
    lastSubmittedConnectionCount = 0;

    constructor(private readonly wrapper: CanvasWrapper) {}

    resize(_request: CanvasRendererHostResize): void {}

    updateFontState(_font: {
        readonly fontFamily: string;
        readonly fontReady: boolean;
        readonly materialIconReady: boolean;
    }): void {}

    drawFrame(request: CanvasRendererFrameRequest): void {
        this.submittedFrameId += 1;
        this.lastSubmittedNodeCount = request.cullPlan.nodes.length;
        this.lastSubmittedConnectionCount = request.cullPlan.connections.length;
        renderCanvasFrame({
            ...request,
            wrapper: this.wrapper,
        });
        this.presentedFrameId = this.submittedFrameId;
    }

    destroy(): void {}
}

export class OffscreenCanvasRendererHost implements CanvasRendererHost {
    readonly kind = 'offscreen' as const;
    submittedFrameId = 0;
    presentedFrameId = 0;
    lastSubmittedNodeCount = 0;
    lastSubmittedConnectionCount = 0;

    constructor(
        private readonly worker: Worker,
        canvas: OffscreenCanvas,
        gridSize: number,
        font: {
            readonly fontFamily: string;
            readonly fontReady: boolean;
            readonly materialIconReady: boolean;
        },
    ) {
        this.worker.addEventListener('message', (event) =>
            this.handleWorkerMessage(event as MessageEvent<CanvasWorkerHostMessage>),
        );
        this.worker.postMessage(
            {
                kind: 'init',
                canvas,
                gridSize,
                font,
            },
            [canvas],
        );
    }

    static create(
        canvas: HTMLCanvasElement,
        gridSize: number,
        font: {
            readonly fontFamily: string;
            readonly fontReady: boolean;
            readonly materialIconReady: boolean;
        },
    ): OffscreenCanvasRendererHost | null {
        if (
            typeof Worker !== 'function' ||
            typeof canvas.transferControlToOffscreen !== 'function'
        ) {
            return null;
        }

        try {
            const worker = new Worker(
                new URL('./canvas-offscreen-render.worker', import.meta.url),
                { type: 'module' },
            );
            const offscreen = canvas.transferControlToOffscreen();
            return new OffscreenCanvasRendererHost(
                worker,
                offscreen,
                gridSize,
                font,
            );
        } catch {
            return null;
        }
    }

    resize(request: CanvasRendererHostResize): void {
        this.worker.postMessage({
            kind: 'resize',
            width: request.width,
            height: request.height,
            devicePixelRatio: request.devicePixelRatio,
        });
    }

    updateFontState(font: {
        readonly fontFamily: string;
        readonly fontReady: boolean;
        readonly materialIconReady: boolean;
    }): void {
        this.worker.postMessage({
            kind: 'font',
            font,
        });
    }

    drawFrame(request: CanvasRendererFrameRequest): void {
        this.submittedFrameId += 1;
        this.lastSubmittedNodeCount = request.cullPlan.nodes.length;
        this.lastSubmittedConnectionCount = request.cullPlan.connections.length;
        this.worker.postMessage({
            kind: 'frame',
            frameId: this.submittedFrameId,
            frame: serializeCanvasWorkerFrame(request),
        });
    }

    destroy(): void {
        this.worker.postMessage({ kind: 'destroy' });
        this.worker.terminate();
    }

    private handleWorkerMessage(
        event: MessageEvent<CanvasWorkerHostMessage>,
    ): void {
        if (event.data.kind === 'frame-presented') {
            this.presentedFrameId = Math.max(
                this.presentedFrameId,
                event.data.frameId,
            );
        }
    }
}
