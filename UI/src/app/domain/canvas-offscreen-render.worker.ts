/// <reference lib="webworker" />

import {
    CanvasWorkerFontState,
    CanvasWorkerFrameMessage,
    CanvasWorkerMessage,
    reviveCanvasWorkerFrame,
} from './canvas-render-worker-contract';
import { renderCanvasFrame } from './canvas-render-frame-renderer';
import { CanvasWrapper } from './canvas-wrapper';

let wrapper: CanvasWrapper | null = null;
let latestFrameId = 0;
let latestFrame: CanvasWorkerFrameMessage | null = null;
let fontLoadGeneration = 0;
let currentFontState: CanvasWorkerFontState | null = null;
let loadingMaterialIconFontUrl: string | null = null;
let loadedMaterialIconFontUrl: string | null = null;

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
                    materialIconReady: isMaterialIconFontReady(data.font),
                    materialIconFontUrl: data.font.materialIconFontUrl,
                    loadDocumentFonts: false,
                },
            );
            currentFontState = data.font;
            ensureWorkerMaterialIconFont(data.font);
            return;
        }
        case 'resize':
            resizeWorkerCanvas(data.width, data.height, data.devicePixelRatio);
            return;
        case 'font':
            updateWorkerFontState(data.font);
            return;
        case 'frame':
            if (!wrapper || data.frameId < latestFrameId) {
                return;
            }

            latestFrameId = data.frameId;
            latestFrame = data;
            renderLatestFrame();
            return;
        case 'destroy':
            wrapper = null;
            close();
            return;
    }
});

function updateWorkerFontState(font: CanvasWorkerFontState): void {
    currentFontState = font;
    wrapper?.updateFontState({
        ...font,
        materialIconReady: isMaterialIconFontReady(font),
    });
    ensureWorkerMaterialIconFont(font);
}

function ensureWorkerMaterialIconFont(font: CanvasWorkerFontState): void {
    if (!font.materialIconFontUrl) {
        loadingMaterialIconFontUrl = null;
        fontLoadGeneration += 1;
        return;
    }

    if (
        font.materialIconFontUrl === loadedMaterialIconFontUrl ||
        font.materialIconFontUrl === loadingMaterialIconFontUrl
    ) {
        return;
    }

    void loadWorkerMaterialIconFont(font);
}

async function loadWorkerMaterialIconFont(
    font: CanvasWorkerFontState,
): Promise<void> {
    const generation = ++fontLoadGeneration;
    loadingMaterialIconFontUrl = font.materialIconFontUrl;
    if (!wrapper || !font.materialIconFontUrl || typeof FontFace === 'undefined') {
        loadingMaterialIconFontUrl = null;
        return;
    }

    try {
        const fontFace = new FontFace(
            'Material Icons',
            `url(${JSON.stringify(font.materialIconFontUrl)}) format("woff2")`,
            {
                style: 'normal',
                weight: '400',
            },
        );
        await fontFace.load();
        if (generation !== fontLoadGeneration || !wrapper) {
            return;
        }

        const fontSet = (globalThis as unknown as { fonts?: FontFaceSet }).fonts;
        fontSet?.add(fontFace);
        await fontSet?.ready;
        const materialIconReady =
            fontSet?.check('400 20px "Material Icons"') ?? true;
        loadingMaterialIconFontUrl = null;
        loadedMaterialIconFontUrl = materialIconReady
            ? font.materialIconFontUrl
            : null;
        const effectiveFont = currentFontState ?? font;
        wrapper.updateFontState({
            ...effectiveFont,
            materialIconReady,
        });
        if (materialIconReady) {
            renderLatestFrame();
        }
    } catch {
        if (generation === fontLoadGeneration && wrapper) {
            loadingMaterialIconFontUrl = null;
            loadedMaterialIconFontUrl = null;
            const effectiveFont = currentFontState ?? font;
            wrapper.updateFontState({
                ...effectiveFont,
                materialIconReady: false,
            });
        }
    }
}

function isMaterialIconFontReady(font: CanvasWorkerFontState): boolean {
    return (
        font.materialIconFontUrl !== null &&
        font.materialIconFontUrl === loadedMaterialIconFontUrl
    );
}

function renderLatestFrame(): void {
    if (!wrapper || !latestFrame) {
        return;
    }

    renderCanvasFrame(reviveCanvasWorkerFrame(latestFrame.frame, wrapper));
    postMessage({ kind: 'frame-presented', frameId: latestFrame.frameId });
}

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
