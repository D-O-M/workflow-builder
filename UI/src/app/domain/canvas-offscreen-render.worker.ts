/// <reference lib="webworker" />

import {
    CanvasWorkerMessage,
    reviveCanvasWorkerFrame,
} from './canvas-render-worker-contract';
import { renderCanvasFrame } from './canvas-render-frame-renderer';
import { CanvasWrapper } from './canvas-wrapper';

let wrapper: CanvasWrapper | null = null;
let latestFrameId = 0;

addEventListener('message', ({ data }: MessageEvent<CanvasWorkerMessage>) => {
    switch (data.kind) {
        case 'init': {
            const context = data.canvas.getContext('2d');
            if (!context) {
                return;
            }

            wrapper = new CanvasWrapper(
                context as unknown as CanvasRenderingContext2D,
                data.gridSize,
                {
                    fontFamily: data.font.fontFamily,
                    fontReady: data.font.fontReady,
                    materialIconReady: data.font.materialIconReady,
                    loadDocumentFonts: false,
                },
            );
            return;
        }
        case 'resize':
            resizeWorkerCanvas(data.width, data.height, data.devicePixelRatio);
            return;
        case 'font':
            wrapper?.updateFontState(data.font);
            return;
        case 'frame':
            if (!wrapper || data.frameId < latestFrameId) {
                return;
            }

            latestFrameId = data.frameId;
            renderCanvasFrame(reviveCanvasWorkerFrame(data.frame, wrapper));
            postMessage({ kind: 'frame-presented', frameId: data.frameId });
            return;
        case 'destroy':
            wrapper = null;
            close();
            return;
    }
});

function resizeWorkerCanvas(
    width: number,
    height: number,
    devicePixelRatio: number,
): void {
    if (!wrapper) {
        return;
    }

    const canvas = wrapper.ctx.canvas as OffscreenCanvas | undefined;
    if (!canvas) {
        return;
    }

    if (canvas.width !== width) {
        canvas.width = width;
    }

    if (canvas.height !== height) {
        canvas.height = height;
    }

    wrapper.ctx.setTransform(1, 0, 0, 1, 0, 0);
    wrapper.ctx.scale(devicePixelRatio, devicePixelRatio);
}
