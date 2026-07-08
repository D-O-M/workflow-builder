import { afterEach, describe, expect, it, vi } from 'vitest';
import { OffscreenCanvasRendererHost } from './canvas-renderer-host';
import { CanvasRenderSnapshot } from './canvas-render-model';
import { CanvasRenderFramePlan } from './canvas-render-frame-factory';

class RecordingWorker {
    static instances: RecordingWorker[] = [];

    readonly messages: Array<{
        readonly message: unknown;
        readonly transfer?: Transferable[];
    }> = [];
    private messageListener:
        | ((event: MessageEvent<{ kind: string; frameId?: number }>) => void)
        | null = null;
    terminated = false;

    constructor(_url: URL, _options: WorkerOptions) {
        RecordingWorker.instances.push(this);
    }

    postMessage(message: unknown, transfer?: Transferable[]): void {
        this.messages.push({ message, transfer });
    }

    addEventListener(
        type: string,
        listener: (event: MessageEvent<{ kind: string; frameId?: number }>) => void,
    ): void {
        if (type === 'message') {
            this.messageListener = listener;
        }
    }

    emitMessage(message: { readonly kind: string; readonly frameId?: number }): void {
        this.messageListener?.({ data: message } as MessageEvent<{
            kind: string;
            frameId?: number;
        }>);
    }

    terminate(): void {
        this.terminated = true;
    }
}

describe('OffscreenCanvasRendererHost', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
        RecordingWorker.instances = [];
    });

    it('returns null when worker or transfer support is unavailable', () => {
        vi.stubGlobal('Worker', undefined);

        const host = OffscreenCanvasRendererHost.create(
            {} as HTMLCanvasElement,
            20,
            {
                fontFamily: 'Inter',
                fontReady: true,
                materialIconReady: false,
            },
        );

        expect(host).toBeNull();
    });

    it('initializes the worker with a transferred offscreen canvas and handles lifecycle messages', () => {
        vi.stubGlobal('Worker', RecordingWorker);
        const offscreen = { width: 0, height: 0 } as OffscreenCanvas;
        const canvas = {
            transferControlToOffscreen: vi.fn(() => offscreen),
        } as unknown as HTMLCanvasElement;

        const host = OffscreenCanvasRendererHost.create(canvas, 20, {
            fontFamily: 'Inter',
            fontReady: true,
            materialIconReady: false,
        });

        expect(host).not.toBeNull();
        const worker = RecordingWorker.instances[0]!;
        expect(worker.messages[0]).toEqual({
            message: {
                kind: 'init',
                canvas: offscreen,
                gridSize: 20,
                font: {
                    fontFamily: 'Inter',
                    fontReady: true,
                    materialIconReady: false,
                },
            },
            transfer: [offscreen],
        });

        host!.resize({ width: 800, height: 600, devicePixelRatio: 2 });
        host!.updateFontState({
            fontFamily: 'Inter',
            fontReady: true,
            materialIconReady: true,
        });
        host!.destroy();

        expect(worker.messages[1]!.message).toEqual({
            kind: 'resize',
            width: 800,
            height: 600,
            devicePixelRatio: 2,
        });
        expect(worker.messages[2]!.message).toEqual({
            kind: 'font',
            font: {
                fontFamily: 'Inter',
                fontReady: true,
                materialIconReady: true,
            },
        });
        expect(worker.messages[3]!.message).toEqual({ kind: 'destroy' });
        expect(worker.terminated).toBe(true);
    });

    it('posts monotonically increasing frame ids so the worker can drop stale frames', () => {
        vi.stubGlobal('Worker', RecordingWorker);
        const offscreen = { width: 0, height: 0 } as OffscreenCanvas;
        const canvas = {
            transferControlToOffscreen: vi.fn(() => offscreen),
        } as unknown as HTMLCanvasElement;
        const host = OffscreenCanvasRendererHost.create(canvas, 20, {
            fontFamily: 'Inter',
            fontReady: true,
            materialIconReady: false,
        })!;
        const request = {
            canvasWidth: 800,
            canvasHeight: 600,
            canvasClientWidth: 400,
            canvasClientHeight: 300,
            offset: { x: 0, y: 0 },
            zoom: 1,
            backgroundEffect: 'plain' as const,
            backdropHoverCanvasPoint: null,
            snapshot: {
                portConnectivity: {
                    summaryPortKeys: new Set<string>(),
                    propertyPortKeys: new Set<string>(),
                },
            } as CanvasRenderSnapshot,
            frame: {
                connectionLayer: {
                    selectedConnectionIds: new Set<string>(),
                    options: {},
                },
                nodeLayer: {
                    selectedNodeIds: new Set<number>(),
                    options: {},
                },
                connectionPreview: null,
                marqueeBounds: null,
                failureTooltip: null,
                hoverTooltip: null,
            } satisfies CanvasRenderFramePlan,
            cullPlan: {
                nodes: [],
                connections: [],
            },
            dragConnection: null,
            hoveredNodeId: null,
            hoveredPortKey: null,
        };

        host.drawFrame(request);
        host.drawFrame(request);

        const worker = RecordingWorker.instances[0]!;
        expect(host.submittedFrameId).toBe(2);
        expect(host.presentedFrameId).toBe(0);
        expect(worker.messages[1]!.message).toMatchObject({
            kind: 'frame',
            frameId: 1,
        });
        expect(worker.messages[2]!.message).toMatchObject({
            kind: 'frame',
            frameId: 2,
        });

        worker.emitMessage({ kind: 'frame-presented', frameId: 1 });
        worker.emitMessage({ kind: 'frame-presented', frameId: 2 });

        expect(host.presentedFrameId).toBe(2);
    });
});
